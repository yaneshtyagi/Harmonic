﻿using Harmonic.Networking;
using Harmonic.Networking.Rtmp.Data;
using Harmonic.Networking.Rtmp.Exceptions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using Harmonic.Networking.Rtmp.Messages;
using Harmonic.Networking.Utils;
using Harmonic.Networking.Rtmp.Serialization;
using Harmonic.Buffers;
using Harmonic.Networking.Amf.Serialization.Amf0;
using Harmonic.Networking.Amf.Serialization.Amf3;
using System.Reflection;
using Harmonic.Networking.Rtmp.Messages.UserControlMessages;
using Harmonic.Networking.Rtmp.Messages.Commands;
using Harmonic.Hosting;
using System.Linq;

namespace Harmonic.Networking.Rtmp
{
    enum ProcessState
    {
        HandshakeC0C1,
        HandshakeC2,
        FirstByteBasicHeader,
        ChunkMessageHeader,
        ExtendedTimestamp,
        CompleteMessage
    }

    // TBD: retransfer bytes when acknowledgement not received
    class IOPipeLine : IDisposable
    {

        internal delegate Task BufferProcessor(ByteBuffer buffer, CancellationToken ct);
        internal SemaphoreSlim _writerSignal = new SemaphoreSlim(0);

        private ByteBuffer _socketBuffer = new ByteBuffer(2048, 32767);
        private Socket _socket;
        private ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        private MemoryPool<byte> _memoryPool = MemoryPool<byte>.Shared;
        private readonly int _resumeWriterThreshole;
        internal Dictionary<ProcessState, BufferProcessor> _bufferProcessors;

        internal Queue<WriteState> _writerQueue = new Queue<WriteState>();
        internal ProcessState NextProcessState { get; set; } = ProcessState.HandshakeC0C1;
        internal ChunkStreamContext ChunkStreamContext { get; set; } = null;
        private HandshakeContext _handshakeContext = null;
        internal RtmpServerOptions _options = null;

        public IOPipeLine(Socket socket, RtmpServerOptions options, int resumeWriterThreshole = 65535)
        {
            _socket = socket;
            _resumeWriterThreshole = resumeWriterThreshole;
            _bufferProcessors = new Dictionary<ProcessState, BufferProcessor>();
            _options = options;
            _handshakeContext = new HandshakeContext(this);
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            var t1 = Producer(_socket, ct);
            var t2 = Consumer(ct);
            var t3 = Writer();
            ct.Register(() =>
            {
                ChunkStreamContext?.Dispose();
                ChunkStreamContext = null;
            });

            var tcs = new TaskCompletionSource<int>();
            t1.ContinueWith(_ =>
            {
                tcs.TrySetException(t1.Exception.InnerException);
            }, TaskContinuationOptions.OnlyOnFaulted);
            t2.ContinueWith(_ =>
            {
                tcs.TrySetException(t2.Exception.InnerException);
            }, TaskContinuationOptions.OnlyOnFaulted);
            t3.ContinueWith(_ =>
            {
                tcs.TrySetException(t3.Exception.InnerException);
            }, TaskContinuationOptions.OnlyOnFaulted);
            t1.ContinueWith(_ =>
            {
                tcs.TrySetCanceled();
            }, TaskContinuationOptions.OnlyOnCanceled);
            t2.ContinueWith(_ =>
            {
                tcs.TrySetCanceled();
            }, TaskContinuationOptions.OnlyOnCanceled);
            t3.ContinueWith(_ =>
            {
                tcs.TrySetCanceled();
            }, TaskContinuationOptions.OnlyOnCanceled);
            t1.ContinueWith(_ =>
            {
                tcs.TrySetResult(1);
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
            t2.ContinueWith(_ =>
            {
                tcs.TrySetResult(1);
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
            t3.ContinueWith(_ =>
            {
                tcs.TrySetResult(1);
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
            return tcs.Task;
        }

        internal void OnHandshakeSuccessful()
        {
            _handshakeContext?.Dispose();
            _handshakeContext = null;
            _bufferProcessors.Clear();
            ChunkStreamContext = new ChunkStreamContext(this);
        }

        #region Sender
        private async Task Writer()
        {
            while (true)
            {
                await _writerSignal.WaitAsync();
                var data = _writerQueue.Dequeue();
                await _socket.SendAsync(data.Buffer.AsMemory(0, data.Length), SocketFlags.None);
                _arrayPool.Return(data.Buffer);
                data.TaskSource?.SetResult(1);
            }
        }
        #endregion

        #region Receiver
        private async Task Producer(Socket s, CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                //var memory = writer.GetMemory(ChunkStreamContext == null ? 1536 : ChunkStreamContext.ReadMinimumBufferSize);
                using (var owner = _memoryPool.Rent(2048))
                {
                    var memory = owner.Memory;
                    var bytesRead = await s.ReceiveAsync(memory, SocketFlags.None);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    if (ChunkStreamContext != null)
                    {
                        ChunkStreamContext.ReadWindowSize += (uint)bytesRead;
                        if (ChunkStreamContext.ReadWindowAcknowledgementSize.HasValue)
                        {
                            if (ChunkStreamContext.ReadWindowSize >= ChunkStreamContext.ReadWindowAcknowledgementSize)
                            {
                                ChunkStreamContext._rtmpSession.Acknowledgement(ChunkStreamContext.ReadWindowAcknowledgementSize.Value);
                                ChunkStreamContext.ReadWindowSize -= ChunkStreamContext.ReadWindowAcknowledgementSize.Value;
                            }
                        }
                    }
                    await _socketBuffer.WriteToBufferAsync(memory);
                }
            }

        }

        private async Task Consumer(CancellationToken ct = default)
        {
            while (ct.IsCancellationRequested)
            {
                await _bufferProcessors[NextProcessState](_socketBuffer, ct);
            }
        }

        internal void Disconnect()
        {
            _socket.Close();
        }
        #endregion

        #region Multiplexing
        internal Task SendRawData(byte[] data, int length)
        {
            var tcs = new TaskCompletionSource<int>();
            _writerQueue.Enqueue(new WriteState()
            {
                Buffer = data,
                Length = length,
                TaskSource = tcs
            });
            _writerSignal.Release();
            return tcs.Task;
        }

        internal Task MultiplexMessageAsync(uint chunkStreamId, Message message)
        {
            return ChunkStreamContext?.MultiplexMessageAsync(chunkStreamId, message);
        }
        #endregion


        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _handshakeContext?.Dispose();
                    ChunkStreamContext?.Dispose();
                    _socket.Dispose();
                }


                disposedValue = true;
            }
        }

        // ~IOPipeline() {
        //   Dispose(false);
        // }

        public void Dispose()
        {
            Dispose(true);
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}