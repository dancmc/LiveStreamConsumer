

using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.MagicLeap;
using Object = System.Object;


[RequireComponent(typeof(PrivilegeRequester))]
    public class LiveStreamConsumer : MonoBehaviour
    {
        
        private PrivilegeRequester _privilegeRequester;

        public RawImage rawImage;
        private Texture2D rawImageTexture;
        
        private Thread sendThread;
        private Thread listenThread;
        private volatile bool socketConnected;
        private volatile bool ssdpReceived;
        TcpClient client = new TcpClient();
        UdpClient uClient;
        private StreamReader clientReader;
        private byte[] imageBuffer1 = new byte[1000000];
        private byte[] imageBuffer2 = new byte[1000000];
        private int currentImageBufferToRead = 1;
        private byte[] disposeBuffer = new byte[1000000];
        private bool waitForRead;

        private bool destroyed;

        
        private int int1 = (int) Math.Pow(256.0, 1.0);
        private int int2 = (int) Math.Pow(256.0, 2.0);
        private int int3 = (int) Math.Pow(256.0, 3.0);
        
        
        private static void MLog(String str)
        {
            Debug.unityLogger.Log(str);
        }


        public LiveStreamConsumer()
        {
            Debug.unityLogger.logEnabled = true;
            
        }


   


        private void Awake()
        {
            // Request privileges
            Debug.Log("Requesting Privileges");
            _privilegeRequester = GetComponent<PrivilegeRequester>();
            _privilegeRequester.OnPrivilegesDone += HandlePrivilegesDone;
            
            rawImageTexture = new Texture2D(960, 540, TextureFormat.RGB24, false);
            rawImage.texture = rawImageTexture;
            
            findNewServer();

            
        }
        
        private void findNewServer()
        {
            MLog("Finding new server");

            if (destroyed) return;

            ssdpReceived = false;
            
            listenThread = new Thread(listenForServiceReply);
            listenThread.IsBackground = true;
            listenThread.Start();

            sendThread = new Thread(sendServiceRequest);
            sendThread.IsBackground = true;
            sendThread.Start();
        }

        private byte[] intBuffer = new byte[4];
         
        
        void receiveStream()
        {
            
            
            while (socketConnected)
            {
                try
                {
                    
                    var b = client.GetStream().Read(intBuffer, 0, 4);
                    
                    if (b < 1)
                    {
                        client.Close();
                        socketConnected = false;
                    }
                    
                    var sizeBytes = byteArrayToInt32(intBuffer);
                    MLog(sizeBytes + " bytes received");
                    if (!waitForRead)
                    {
                        var currentBuffer = getCurrentBuffer();
                        Array.Clear(currentBuffer,0, currentBuffer.Length);
                        client.GetStream().Read(currentBuffer, 0, sizeBytes);
                        waitForRead = true;
                    }
                    else
                    {
                        client.GetStream().Read(disposeBuffer, 0, sizeBytes);
                    }
                }
                catch (Exception e)
                {
                    MLog(e.Message);
                    
                    
                    closeClient();
                    socketConnected = false;
                    
                    findNewServer();
                    
                }
                
            }

        }

        private void Update()
        {
            if (waitForRead)
            {
                rawImageTexture.LoadImage(getCurrentBuffer());
                rawImageTexture.Apply();
                swapCurrentBuffer();
                waitForRead = false;
            }
        }

        private byte[] getCurrentBuffer()
        {
            if (currentImageBufferToRead == 1)
            {
                return imageBuffer1;
            }
            else
            {
                return imageBuffer2;
            }
        }

        private void swapCurrentBuffer()
        {
            if (currentImageBufferToRead == 1)
            {
                currentImageBufferToRead = 2;
            }
            else
            {
                currentImageBufferToRead = 1;
            }
        }

        private void closeClient()
        {
            try
            {
                client.Close();
            }
            catch (ObjectDisposedException e)
            {
                MLog(e.Message);
            }
        }

        private int byteArrayToInt32(byte[] ba)
        {
            return ba[0] + ba[1] * int1 + ba[2] * int2 + ba[3] * int3;
        }

        private void OnDestroy()
        {
            destroyed = true;
            if (_privilegeRequester != null) _privilegeRequester.OnPrivilegesDone -= HandlePrivilegesDone;
            closeClient();
            sendThread.Interrupt();
            listenThread.Interrupt();
        }


        

        private void DisableCamera()
        {
            if (MLCamera.IsStarted)
            {
                CameraScript.setCaptureActive(false);
                MLCamera.Disconnect();
                MLCamera.Stop();
            }
        }

        private void EnableCapture()
        {
                // Enable camera and set controller callbacks
                MLCamera.Start();
                MLCamera.Connect();
                
                CameraScript.setCaptureActive(true);

        }




        private void HandlePrivilegesDone(MLResult result)
        {
            if (!result.IsOk)
            {
                if (result.Code == MLResultCode.PrivilegeDenied) Instantiate(Resources.Load("PrivilegeDeniedError"));

                Debug.LogErrorFormat(
                    "Error: LiveStreamConsumer failed to get all requested privileges, disabling script. Reason: {0}",
                    result);
                
                return;
            }

            Debug.Log("Succeeded in requesting all privileges");
            
            EnableCapture();
            

        }

        void sendServiceRequest()
        {
            Thread.Sleep(2000);
            var udpClient = new UdpClient("239.255.255.250", 1900);
            byte[] request = Encoding.UTF8.GetBytes("ml-stream-locate");
            
            while (!ssdpReceived)
            {
                udpClient.Send(request, request.Length);
                Thread.Sleep(3000);
            }
        
            udpClient.Close();
        }

        void listenForServiceReply()
        {


            var udpClient = new UdpClient();
            IPEndPoint localEp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpClient.Client.Bind(localEp);
            IPAddress multicastAddress = IPAddress.Parse("239.255.255.250");
            udpClient.JoinMulticastGroup(multicastAddress);

            while (true)
            {
                byte[] data = udpClient.Receive(ref localEp);
                var message = Encoding.UTF8.GetString(data, 0, data.Length);

                if (!message.ToLower().StartsWith("ml-stream-server")) continue;
                MLog(message);
            
                var parts = message.Split(' ');
                if (parts.Length < 2) continue;

                var address = parts[1].Split(':');

                ssdpReceived = true;

                closeClient();
                client = new TcpClient();
                client.Connect(address[0], Convert.ToInt32(address[1]));
                var initialMessage = Encoding.UTF8.GetBytes("consumer\n");
                client.GetStream().Write(initialMessage,0, initialMessage.Length);
                var reader = new StreamReader(client.GetStream());
                MLog(reader.ReadLine());
            
                socketConnected = true;
                receiveStream();
                
                break;
            }
        }


        
    }
