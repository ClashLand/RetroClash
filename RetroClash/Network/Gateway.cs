﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RetroClash.Logic;
using RetroClash.Protocol;

namespace RetroClash.Network
{
    public class Gateway
    {
        private readonly SocketAsyncEventArgsPool _acceptPool = new SocketAsyncEventArgsPool();
        private readonly Pool<byte[]> _bufferPool = new Pool<byte[]>();
        private readonly SocketAsyncEventArgsPool _writeReadArgsPool = new SocketAsyncEventArgsPool();
        public int ConnectedSockets;

        public Gateway()
        {
            try
            {
                Listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ReceiveBufferSize = Configuration.BufferSize,
                    SendBufferSize = Configuration.BufferSize,
                    Blocking = false,
                    NoDelay = true
                };

                for (var i = 0; i < Configuration.MaxClients; i++)
                {
                    var readWriteEventArgs = new SocketAsyncEventArgs();
                    readWriteEventArgs.Completed += OnIoCompleted;
                    _writeReadArgsPool.Enqueue(readWriteEventArgs);
                }

                for (var i = 0; i < Configuration.OpsToPreAlloc; i++)
                {
                    var acceptEvent = new SocketAsyncEventArgs();
                    acceptEvent.Completed += OnIoCompleted;
                    _acceptPool.Enqueue(acceptEvent);
                }

                Listener.Bind(new IPEndPoint(IPAddress.Any, Resources.Configuration.ServerPort));
                Listener.Listen(Configuration.MaxClients);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"RetroClash is listening on {Listener.LocalEndPoint}. Let's play Clash of Clans!");
                Console.ResetColor();

                StartAccept().Wait();
            }
            catch (Exception exception)
            {
                Logger.Log(exception, Enums.LogType.Error);
            }
        }

        public Socket Listener { get; set; }

        public byte[] GetBuffer => _bufferPool.Pop ?? new byte[Configuration.BufferSize];

        public async Task StartAccept()
        {
            var acceptEvent = _acceptPool.Dequeue();
            if (acceptEvent == null)
            {
                acceptEvent = new SocketAsyncEventArgs();
                acceptEvent.Completed += OnIoCompleted;
            }
            while (true)
                if (!Listener.AcceptAsync(acceptEvent))
                    await ProcessAccept(acceptEvent, false);
                else
                    break;
        }

        public async Task ProcessAccept(SocketAsyncEventArgs asyncEvent, bool startNew)
        {
            var socket = asyncEvent.AcceptSocket;

            if (asyncEvent.SocketError == SocketError.Success)
            {
                var readEvent = _writeReadArgsPool.Dequeue();

                if (readEvent == null)
                {
                    readEvent = new SocketAsyncEventArgs();

                    readEvent.Completed += OnIoCompleted;
                }

                try
                {
                    var buffer = GetBuffer;

                    readEvent.SetBuffer(buffer, 0, buffer.Length);

                    readEvent.AcceptSocket = socket;

                    var device = new Device(socket);
                    device.Token = new UserToken(readEvent, device);

                    Interlocked.Increment(ref ConnectedSockets);

                    await StartReceive(readEvent);
                }
                catch (Exception)
                {
                    Disconnect(asyncEvent);
                }
            }
            else
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                }
                catch (Exception exception)
                {
                    Logger.Log(exception, Enums.LogType.Error);
                }
            }

            asyncEvent.AcceptSocket = null;
            _acceptPool.Enqueue(asyncEvent);

            if (startNew)
                await StartAccept();
        }

        public async Task ProcessReceive(SocketAsyncEventArgs asyncEvent, bool startNew)
        {
            if (asyncEvent.BytesTransferred == 0 || asyncEvent.SocketError != SocketError.Success)
            {
                Disconnect(asyncEvent);
                Recycle(asyncEvent);
            }
            else
            {
                var token = (UserToken) asyncEvent.UserToken;

                await token.SetData();

                try
                {
                    if (token.Device.Socket.Available == 0)
                        await token.Device.ProcessPacket(token.Stream.ToArray());
                }
                catch (Exception exception)
                {
                    Logger.Log(exception, Enums.LogType.Error);
                }

                if (startNew)
                    await StartReceive(asyncEvent);
            }
        }

        public async Task StartReceive(SocketAsyncEventArgs asyncEvent)
        {
            try
            {
                while (true)
                    if (!((UserToken) asyncEvent.UserToken).Device.Socket.ReceiveAsync(asyncEvent))
                        await ProcessReceive(asyncEvent, false);
                    else
                        break;
            }
            catch (ObjectDisposedException)
            {
                Recycle(asyncEvent);
            }
            catch (Exception)
            {
                Disconnect(asyncEvent);
            }
        }

        public void Disconnect(SocketAsyncEventArgs asyncEvent)
        {
            if (asyncEvent == null) return;
            try
            {
                Interlocked.Decrement(ref ConnectedSockets);

                var token = (UserToken) asyncEvent.UserToken;

                if (token.Device.Player != null)
                    Resources.PlayerCache.RemovePlayer(token.Device.Player.AccountId);
            }
            catch (Exception exception)
            {
                Logger.Log(exception, Enums.LogType.Error);
            }
        }

        public async Task Send(Message message)
        {
            try
            {
                var writeEvent = _writeReadArgsPool.Dequeue();

                if (writeEvent == null)
                {
                    writeEvent = new SocketAsyncEventArgs();

                    writeEvent.Completed += OnIoCompleted;
                }

                await message.Encode();

                /*if (Configuration.Debug)
                    Console.WriteLine(message.ToString());*/

                message.Encrypt();

                writeEvent.SetBuffer(await message.BuildPacket(), 0, message.Length + 7);

                writeEvent.AcceptSocket = message.Device.Socket;
                writeEvent.RemoteEndPoint = message.Device.Socket.RemoteEndPoint;
                writeEvent.UserToken = message.Device.Token;

                message.Dispose();

                await StartSend(writeEvent);
            }
            catch (Exception exception)
            {
                Logger.Log(exception, Enums.LogType.Error);
            }
        }

        public async Task StartSend(SocketAsyncEventArgs asyncEvent)
        {
            var client = (UserToken) asyncEvent.UserToken;
            var socket = client.Device.Socket;

            try
            {
                while (true)
                    if (!socket.SendAsync(asyncEvent))
                        await ProcessSend(asyncEvent);
                    else
                        break;
            }
            catch (ObjectDisposedException)
            {
                Recycle(asyncEvent, false);
            }
            catch (Exception)
            {
                Disconnect(asyncEvent);
            }
        }

        public async Task ProcessSend(SocketAsyncEventArgs args)
        {
            var transferred = args.BytesTransferred;
            if (transferred == 0 || args.SocketError != SocketError.Success)
            {
                Disconnect(args);
                Recycle(args, false);
            }
            else
            {
                try
                {
                    var count = args.Count;
                    if (transferred < count)
                    {
                        args.SetBuffer(transferred, count - transferred);
                        await StartSend(args);
                    }
                    else
                    {
                        Recycle(args, false);
                    }
                }
                catch (Exception)
                {
                    Disconnect(args);
                }
            }
        }

        public async void OnIoCompleted(object sender, SocketAsyncEventArgs asyncEvent)
        {
            switch (asyncEvent.LastOperation)
            {
                case SocketAsyncOperation.Accept:
                    await ProcessAccept(asyncEvent, true);
                    break;
                case SocketAsyncOperation.Receive:
                    await ProcessReceive(asyncEvent, true);
                    break;
                case SocketAsyncOperation.Send:
                    await ProcessSend(asyncEvent);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }
        }

        public void Recycle(SocketAsyncEventArgs asyncEvent, bool read = true)
        {
            if (asyncEvent == null)
                return;

            var buffer = asyncEvent.Buffer;
            asyncEvent.UserToken = null;
            asyncEvent.SetBuffer(null, 0, 0);
            asyncEvent.AcceptSocket = null;

            _writeReadArgsPool.Enqueue(asyncEvent);

            Recycle(buffer);
        }

        public void Recycle(byte[] buffer)
        {
            if (buffer?.Length == Configuration.BufferSize)
                _bufferPool.Push(buffer);
        }
    }
}