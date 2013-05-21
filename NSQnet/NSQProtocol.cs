﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NSQnet
{
    public class NSQProtocol
    {
        public static Boolean CheckName(String name)
        {
            return name.Length > 1 && name.Length < MAX_NAME_LENGTH && System.Text.RegularExpressions.Regex.IsMatch(name, VALID_NAME_EXPR);
        }

        public NSQProtocol()
        {
            this.HeartbeatInterval = 30 * 1000; //the default, 30 seconds.
            this.MaximumReadyCount = 2500; 
        }

        public NSQProtocol(String hostname, Int32 port) : this()
        {
            this.Hostname = hostname;
            this.Port = port;
            this.Initialize();
        }

        public NSQProtocol(String hostname, Int32 port, Stream output) : this()
        {
            this.Hostname = hostname;
            this.Port = port;
            this.OutputStream = output;
        }

        public String Hostname { get; set; }
        public Int32 Port { get; set; }

        public Int32 HeartbeatInterval { get; set; }
        public Int64 MaximumReadyCount { get; set; }

        public Stream OutputStream { get; set; }
        private StreamWriter _outputWriter { get; set; }

        private static readonly Byte[] Version = new Byte[4] { 0x20, 0x20, 0x56, 0x32 };
        private static readonly Int16 MAX_NAME_LENGTH = 32;
        private static readonly String VALID_NAME_EXPR = "[.a-zA-Z0-9_-]";

        private Boolean _continue = true;

        private System.Net.Sockets.TcpClient _client = null;
        private System.Net.Sockets.NetworkStream _networkStream = null;
        private System.IO.BinaryReader _networkReader = null;

        private Object _nl = new Object();

        public void Initialize()
        {
            if (String.IsNullOrWhiteSpace(this.Hostname))
                throw new Exception("Hostname must be set.");

            if (this.Port == default(Int16))
                throw new Exception("Port must be set.");

            _client = new System.Net.Sockets.TcpClient();
            _client.Connect(hostname: this.Hostname, port: this.Port);
            _networkStream = _client.GetStream();
            _networkReader = new System.IO.BinaryReader(_networkStream);
            _networkStream.Write(Version, 0, Version.Length);

            if (OutputStream != null)
                _outputWriter = new StreamWriter(OutputStream);

            RecieveLoop();
        }

        public void DestroyConnection()
        {
            _continue = false;
            Close();
            if (_networkStream != null)
            {
                _networkStream.Dispose();
                _networkStream = null;
            }
            if (_client != null)
            {
                _client = null;
            }
        }

        #region Message Wait Loop
        private async void RecieveLoop()
        {
            while (_continue)
            {
                var message = await ReceiveMessageAsync();
                RouteMessage(message);
            }
        }

        public Task<NSQMessage> ReceiveMessageAsync()
        {
            return Task.Run<NSQMessage>(() => ReceiveMessage());
        }

        private NSQMessage ReceiveMessage()
        {
            byte[] sizebuffer = _networkReader.ReadBytes(4);
            if (sizebuffer != null && sizebuffer.Length == 4)
            {
                Array.Reverse(sizebuffer);
                var size = BitConverter.ToInt32(sizebuffer, 0);

                if (size != 0)
                {
                    byte[] buffer = new Byte[size];
                    _networkStream.Read(buffer, 0, (int)size);

                    return UnpackMessage(size, buffer);
                }
                return null;
            }
            return null;
        }

        private void RouteMessage(NSQMessage result)
        {
            if (result == null)
                return;

            if (result.Body.Equals(NSQResponseString.HEARTBEAT))
            {
                NOP();
            }
            else if (result.FrameType == FrameType.Message)
            {
                OnNSQMessageRecieved(new NSQMessageEventArgs() { Message = result });
            }
            
            OnNSQAnyMessageRecieved(new NSQMessageEventArgs() { Message = result });
        }

        private NSQMessage GetNextMessage()
        {
            var result = new NSQMessage();
            Boolean hasFired = false;
            Action<object, NSQMessageEventArgs> action = (obj, e) =>
            {
                result = e.Message;
                hasFired = true;
            };
            var handler = new NSQMessageRecievedHandler(action);
            NSQAnyMessageRecieved += handler;

            while (!hasFired)
            {
                Thread.Sleep(10);
            }

            NSQAnyMessageRecieved -= handler;

            return result;
        }

        void NSQProtocol_NSQAnyMessageRecieved(object sender, NSQMessageEventArgs e)
        {
            throw new NotImplementedException();
        }

        public event NSQMessageRecievedHandler NSQMessageRecieved;

        public void OnNSQMessageRecieved(NSQMessageEventArgs e)
        {
            if (NSQMessageRecieved != null)
                NSQMessageRecieved(this, e);
        }

        public event NSQMessageRecievedHandler NSQAnyMessageRecieved;

        public void OnNSQAnyMessageRecieved(NSQMessageEventArgs e)
        {
            if (NSQAnyMessageRecieved != null)
                NSQAnyMessageRecieved(this, e);
        }
        #endregion

        public NSQMessage Identify(String short_id, String long_id, Int32 heartbeat_interval, Boolean feature_negotiation)
        {
            dynamic json = new AgileObject();
            json.short_id = short_id;
            json.long_id = long_id;
            json.heartbeat_interval = heartbeat_interval;
            json.feature_negotiation = feature_negotiation;

            var jsonText = JsonSerializer.Current.SerializeObject(json);
            WriteAscii("IDENTIFY\n");
            WriteBinary(PackMessage(jsonText));

            var result = GetNextMessage();
            return result;
        }

        public void Subscribe(String topic_name, String channel_name)
        {
            if (!CheckName(topic_name) || !CheckName(channel_name))
                throw new ArgumentException("Bad Name");

            WriteAscii(String.Format("SUB {0} {1}\n", topic_name, channel_name));
            //var result = GetNextMessage();
            //return result;
        }

        public NSQMessage Publish(String topic_name, object data)
        {
            if (!CheckName(topic_name))
                throw new Exception("Bad topic_name");

            var json = JsonSerializer.Current.SerializeObject(data);
            var bytes = PackMessage(json);
   
            WriteAscii(String.Format("PUB {0}\n", topic_name));
            WriteBinary(bytes);

            var result = GetNextMessage();
            return result;
        }

        public NSQMessage MultiPublish(String topic_name, List<Object> data)
        {
            if (!CheckName(topic_name))
                throw new Exception("Bad topic_name");

            WriteAscii(String.Format("MPUB {0}\n", topic_name));

            //TODO: WRITE OBJECTS

            return GetNextMessage();
        }

        public void Ready(Int64 count)
        {
            if (count < 1 || count > this.MaximumReadyCount)
                throw new ArgumentException("Out of bounds", "count");

            WriteAscii(String.Format("RDY {0}\n", count));
        }

        public NSQMessage Finish(String message_id)
        {
            WriteAscii(String.Format("FIN {0}\n", message_id));

            return GetNextMessage();
        }

        public NSQMessage Requeue(String message_id, Int32 timeout)
        {
            //TODO: check timeout.
            WriteAscii(String.Format("REQ {0} {1}\n", message_id, timeout));
            return GetNextMessage();
        }

        public NSQMessage Touch(String message_id)
        {
            WriteAscii(String.Format("TOUCH {0}\n", message_id));
            return GetNextMessage();
        }

        public NSQMessage Close()
        {
            WriteAscii("CLS\n");
            return GetNextMessage();
        }

        public void NOP()
        {
            WriteAscii("NOP\n");
        }

        private void WriteBinary(Byte[] binary)
        {
            _networkStream.Write(binary, 0, binary.Length);
        }

        private void WriteAscii(String unicode)
        {
            var asciiBytes = ConvertToAscii(unicode);
            _networkStream.Write(asciiBytes, 0, asciiBytes.Length);
        }

        public void LogOutput(String output)
        {
            if (_outputWriter != null)
            {
                _outputWriter.Write(output);
                _outputWriter.Flush();
            }
            else
            {
                Debug.Write(output);
            }
        }

        private static Byte[] ConvertToAscii(String unicode)
        {
            var bytes = System.Text.Encoding.Default.GetBytes(unicode);
            return System.Text.Encoding.Convert(System.Text.Encoding.Default, System.Text.Encoding.ASCII, bytes);
        }

        private static String ConvertFromAscii(Byte[] bytes)
        {
            return System.Text.Encoding.Default.GetString(System.Text.Encoding.Convert(System.Text.Encoding.ASCII, System.Text.Encoding.Default, bytes));
        }

        private static Byte[] PackMessage(String text)
        {
            byte[] textBytes = ConvertToAscii(text);
            var size = textBytes.Length;

            byte[] preBuffer = BitConverter.GetBytes(size);

            byte[] output = new byte[4 + size];
            Array.Reverse(preBuffer); //Endian Swap ...
            Array.Copy(preBuffer, output, 4);
            Array.Copy(textBytes, 0, output, 4, size);

            return output;
        }

        private static Byte[] PackMessage(FrameType type, String text)
        {
            byte[] textBytes = ConvertToAscii(text);
            var size = textBytes.Length;

            byte[] sizeBuffer = BitConverter.GetBytes(size);
            Array.Reverse(sizeBuffer); //Endian Swap ...

            byte[] frameTypeBuffer = BitConverter.GetBytes((int)type);
            Array.Reverse(frameTypeBuffer);

            byte[] output = new byte[8 + size];
            Array.Copy(sizeBuffer, output, 4);
            Array.Copy(frameTypeBuffer, 0, output, 4, 4);
            Array.Copy(textBytes, 0, output, 8, size);

            return output;
        }

        private static NSQMessage UnpackMessage(Int32 size, Byte[] buffer)
        {
            byte[] frameTypeBuffer = new Byte[4];
            Array.Copy(buffer, frameTypeBuffer, 4);
            Array.Reverse(frameTypeBuffer);
            FrameType frameType = (FrameType)BitConverter.ToInt32(frameTypeBuffer, 0);

            if (frameType != FrameType.Message)
            {
                byte[] bodyBuffer = new Byte[size - 4];
                Array.Copy(buffer, 4, bodyBuffer, 0, (int)size - 4);
                String result = ConvertFromAscii(bodyBuffer);

                return new NSQMessage() { Size = size, FrameType = frameType, Body = result };
            }
            else
            {
                int cursor = 4;
                int length = 8;

                byte[] timeStampBuffer = new Byte[length];
                Array.Copy(buffer, cursor, timeStampBuffer, 0, length);
                Array.Reverse(timeStampBuffer);
                var timestamp = BitConverter.ToInt64(timeStampBuffer, 0);
                cursor += length;

                length = 2;
                byte[] attemptBuffer = new Byte[length];
                Array.Copy(buffer, cursor, attemptBuffer, 0, length);
                Array.Reverse(attemptBuffer);
                var attempts = BitConverter.ToInt16(attemptBuffer, 0);
                cursor += length;

                length = 16;
                byte[] messageIdBuffer = new Byte[length];
                Array.Copy(buffer, cursor, messageIdBuffer, 0, length);
                var messageId = ConvertFromAscii(messageIdBuffer);
                cursor += length;

                length = size - cursor;
                byte[] bodyBuffer = new Byte[length];
                Array.Copy(buffer, cursor, bodyBuffer, 0, length);
                String body = ConvertFromAscii(bodyBuffer);

                return new NSQMessage() { Size = size, FrameType = frameType, TimeStamp = new DateTime(timestamp), Attempts = attempts, MessageId = messageId, Body = body };
            }
        }
    }
}
