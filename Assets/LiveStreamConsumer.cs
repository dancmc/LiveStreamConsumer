

using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.XR.MagicLeap;

/// <summary>
/// Main class for the demo stream consumer application.
/// Requests privileges, then automatically finds and connects to the desktop application.
/// </summary>
[RequireComponent(typeof(PrivilegeRequester))]
    public class LiveStreamConsumer : MonoBehaviour
    {
        
        private PrivilegeRequester privilegeRequester;

        // Rawimage for displaying jpg stream
        public RawImage rawImage;
        private Texture2D rawImageTexture;

        // Networking and network discovery objects
        private TcpClient client = new TcpClient();
        private volatile bool shouldFindNewServer = false;
        private volatile bool socketConnected;
        private Thread connectionThread;
        private UnityWebRequest discoveryRequest;
        private bool connectionAttemptFinished = true;
        
        // Manually receive and interpret 4 byte int messages
        private byte[] intBuffer = new byte[4];
        private Thread receiveThread;
        private ReceiveMode receiveMode = ReceiveMode.INT;
        
        
        // Swap buffers to prevent writing and reading jpg arrays at the same time
        private byte[] imageBuffer1 = new byte[1000000];
        private byte[] imageBuffer2 = new byte[1000000];
        private int currentImageBufferToRead = 1;
        // If stream is reading faster than JPG decoding, drop frames by reading into dummy buffer
        private byte[] disposeBuffer = new byte[1000000];
        private volatile bool waitForRead;
        
        private Thread writeThread;
 
        
        // Flag to indicate application termination
        // Because Unity editor is not great at cleaning up threads
        private bool destroyed;

        // Constants for the conversion of 4 byte ints
        private int int1 = (int) Math.Pow(256.0, 1.0);
        private int int2 = (int) Math.Pow(256.0, 2.0);
        private int int3 = (int) Math.Pow(256.0, 3.0);
        
        // Set logging on or off (logging introduces lag)
        private const bool logEnabled = true;

        
        //============================================================================================
        //   
        // LIFECYCLE METHODS
        //
        //=============================================================================================

        public LiveStreamConsumer()
        {
            Debug.unityLogger.logEnabled = logEnabled;
        }
  

        private void Awake()
        {
            
            // Request privileges (primarily LAN permission)
            MLog("Requesting Privileges");
            privilegeRequester = GetComponent<PrivilegeRequester>();
            privilegeRequester.OnPrivilegesDone += handlePrivilegesDone;
            
            // Initialise raw image display
            rawImageTexture = new Texture2D(960, 540, TextureFormat.RGB24, false);
            rawImage.texture = rawImageTexture;    

            StartCoroutine(displayJpgs());
            StartCoroutine(monitorConnection());
        }
        
        // Load received jpgs with continuously swapped buffers to avoid corrupting jpgs by reading
        // to byte array while image is being loaded
        IEnumerator displayJpgs()
        {
            while (true)
            {
                yield return new WaitUntil(() => waitForRead);
                
                MLog("displayJpgs :: Loading JPG");
                rawImageTexture.LoadImage(getCurrentBuffer());
                rawImageTexture.Apply();
                swapCurrentBuffer();
                waitForRead = false;
            }
        }

       
        // Start server discovery once privileges have been requested
        private void handlePrivilegesDone(MLResult result)
        {
            if (!result.IsOk)
            {
                if (result.Code == MLResultCode.PrivilegeDenied) 
                    Instantiate(Resources.Load("PrivilegeDeniedError"));

                Debug.LogErrorFormat(
                    "handlePrivilegesDone :: LiveStreamConsumer failed to get all requested " +
                    "privileges, disabling script. Reason: {0}",
                    result);
                
                // Ideally would place a return here, but omitted in interest of making it
                // easy to run on both desktop and ML device without changing variables
            }

            MLog("handlePrivilegesDone :: Succeeded in requesting all privileges");

            shouldFindNewServer = true;
        }

        private void OnDestroy()
        {
            destroyed = true;
            if (privilegeRequester != null) privilegeRequester.OnPrivilegesDone -= handlePrivilegesDone;
            closeClient();
            
            ssdpRequestThread?.Interrupt();
            ssdpListenThread?.Interrupt();
            connectionThread?.Interrupt();
            receiveThread?.Interrupt();
        }
        
        
        //============================================================================================
        //   
        // NETWORK METHODS
        //
        //=============================================================================================
        
        // Coroutine to check if should find new server (necessary because reconnections are initiated
        // on a non-main thread but findNewServer must run on main thread
        IEnumerator monitorConnection()
        {
            while (true)
            {
                yield return new WaitUntil(() => shouldFindNewServer);
                shouldFindNewServer = false;
                findNewServer();
            }
        }

        // Discover server application address (demo assumes only one server active)        
        private void findNewServer()
        {
            MLog("findNewServer :: Discovering server address");
            
            if (destroyed) return;

            // Http call to a private server to obtain a previously registered server address
            StartCoroutine(getServerAddress());
            
            
            // This section is the alternative method - automatic service discovery. Works on desktop,
            // but headsets cannot seem to send or receive UDP multicasts
            
            // listenThread = new Thread(listenForServiceReply);
            // listenThread.IsBackground = true;
            // listenThread.Start();
            
            // sendThread = new Thread(sendServiceRequest);
            // sendThread.IsBackground = true;
            // sendThread.Start();
        }
        
        
        // Coroutine to attempt fetching pre-registered server address at intervals.
        // Once valid address received, attempt to connect then start receiving stream.
        // The connection logic unfortunately cannot be easily delegated to one thread because
        // UnityWebRequest must be called on the main thread, necessitating a complicated 
        // workaround.
        IEnumerator getServerAddress()
        {
        
            while (!socketConnected)
            {
                MLog("getServerAddress :: Making discovery request");
                
                discoveryRequest = UnityWebRequest.Get("https://danielchan.io/mldiscovery/get");
                yield return discoveryRequest.SendWebRequest();
              
                if(discoveryRequest.isNetworkError || discoveryRequest.isHttpError) {
                    MLog("getServerAddress :: Error - "+discoveryRequest.error);
                }

                
                var message = discoveryRequest.downloadHandler.text;
                MLog("getServerAddress :: Received server address - "+message);
            
                var address = message.Split(':');
                var ip = address[0];
                var port = Convert.ToInt32(address[1]);
                if(ip.Equals("") || port<1) continue;

                connectionAttemptFinished = false;
                connectionThread = new Thread(()=>connectToServerBlocking(ip, port));
                connectionThread.IsBackground = true;
                connectionThread.Start();
            
                yield return new WaitUntil(() => connectionAttemptFinished);
                MLog("getServerAddress :: Finished connection attempt");

                yield return new WaitForSeconds(3);
                    
            
            }

            receiveThread?.Interrupt();
            writeThread?.Interrupt();
            
            receiveThread = new Thread(receiveStream);
            receiveThread.IsBackground = true;
            receiveThread.Start();
            
            writeThread = new Thread(writeStream);
            writeThread.IsBackground = true;
            writeThread.Start();

        }
        
        // Connect to server once address discovered
        void connectToServerBlocking(string ip, int port)
        {
            closeClient();
        
            try{
                client = new TcpClient();
                client.Connect(ip, port);
                var initialMessage = Encoding.UTF8.GetBytes("consumer\n");
                client.GetStream().Write(initialMessage, 0, initialMessage.Length);
                var reader = new StreamReader(client.GetStream());
                var response = reader.ReadLine();
                if (response.Contains("rejected"))
                {
                    closeClient();
                }
                else
                {
                    socketConnected = true;
                }
                MLog("connectToServerBlocking :: Response from server - "+response);
            }
            catch (Exception e)
            {
                MLog("connectToServerBlocking :: Error -"+e.Message);
            }

            connectionAttemptFinished = true;
            connectionThread = null;
        }
        
        // Convenience method to close tcp client safely
        private void closeClient()
        {
            try
            {
                client.Close();
                socketConnected = false;
            }
            catch (ObjectDisposedException e)
            {
                MLog("closeClient :: Error -"+e.Message);
            }
        }


        //============================================================================================
        //   
        // STREAM METHODS
        //
        //=============================================================================================
        
        // Receive stream on separate thread.
        // Alternately read size of next frame (as 4 byte int), then the frame itself
        void receiveStream()
        {
            try
            {
                client.GetStream().ReadTimeout = 600000;
                
                readExactFromStream(client.GetStream(), intBuffer, 4);
                receiveMode = ReceiveMode.JPG;
        
                while (socketConnected)
                {
                    if (receiveMode == ReceiveMode.JPG)
                    {

                        int sizeBytes = byteArrayToInt32(intBuffer);
                       
                        MLog("receiveStream :: "+sizeBytes + " bytes received");
                        MLog("receiveStream :: int bytes "+intBuffer[0] + " " + intBuffer[1] + " " + intBuffer[2] + " " + intBuffer[3]);

                        if (!waitForRead)
                        {
                            var currentBuffer = getCurrentBuffer();
                            Array.Clear(currentBuffer, 0, currentBuffer.Length);
//                         client.GetStream().Read(currentBuffer, 0, sizeBytes);
                            readExactFromStream(client.GetStream(), currentBuffer, sizeBytes);
                            
                            waitForRead = true;
                        }
                        else
                        {
//                          client.GetStream().Read(disposeBuffer, 0, sizeBytes);
                            readExactFromStream(client.GetStream(), disposeBuffer, sizeBytes);
                        }
                        receiveMode = ReceiveMode.INT;
                    }
                    else if (receiveMode == ReceiveMode.INT)
                    {
                        MLog("receiveStream :: Reading int bytes");
                        
//                      client.GetStream().Read(intBuffer, 0, 4);
                        readExactFromStream(client.GetStream(), intBuffer, 4);

                        receiveMode = ReceiveMode.JPG;
                    }

                }
            }catch (Exception e)
            {
                MLog("receiveStream :: Error - "+e.Message);
                     
                closeClient();
                socketConnected = false;

                shouldFindNewServer = true;
            }

        }

        // This is necessary to detect disconnections on the server end
        // Writing to a remotely disconnected connection triggers a socket error,
        // whereas reading just waits till timeout (opposite to Java)
        void writeStream()
        {
            try
            {
                while (socketConnected)
                {
                    var message = Encoding.UTF8.GetBytes("p\n");
                    client.GetStream().Write(message, 0, message.Length);
                    Thread.Sleep(2000);
                    
                }
            }catch (Exception e)
            {
                MLog("writeStream :: Error - "+e.Message);

            }
            
        }

        private enum ReceiveMode
        {
            INT, JPG
        }
        
        

        private byte[] getCurrentBuffer()
        {
            switch (currentImageBufferToRead)
            {
                case 1: return imageBuffer1;
                case 2: return imageBuffer2;
                default: return imageBuffer1;
            }
        }

        private void swapCurrentBuffer()
        {
            switch (currentImageBufferToRead)
            {
                case 1: 
                    currentImageBufferToRead = 2;
                    break;
                case 2:
                    currentImageBufferToRead = 1;
                    break;
                default:
                    currentImageBufferToRead = 1;
                    break;
                    
            }
        }

 
        
        //============================================================================================
        //   
        // UTILITY METHODS
        //
        //=============================================================================================
        
        // Convenience method for logging. The Unity logger can however introduce significant delay 
        private static void MLog(string str)
        {
            Debug.unityLogger.Log(str);
        }
        
        // Convenience method to manually convert array of 4 bytes to int (little endian byte order)
        private int byteArrayToInt32(byte[] ba)
        {
            return ba[0] + ba[1] * int1 + ba[2] * int2 + ba[3] * int3;
        }
        
        private void readExactFromStream(Stream stream, byte[] byteBuffer, int length)
        {
            int read = 0;
            
            while (read < length)
            {
                int bytes = stream.Read(byteBuffer, read, length - read);
                read += bytes;
            }
            
        }
        
        
        //============================================================================================
        //   
        // DISCARDED CODE
        //
        // Code samples for selected approaches that have been tried and rejected. Included for possible
        // reuse in future development.
        //
        //=============================================================================================
        
        
        // :::::: SSDP (Simple Service Discovery Protocol) ::::::
        // Attempt to use UDP Multicast to well-known multicast address to request server details.
        // Worked using desktop applications, but unable to replicate on headset (eg unable to receive 
        // even other background chatter on UniWireless or private router network). 
        
        private Thread ssdpRequestThread;
        private Thread ssdpListenThread;
        
        // UDP Multicast to request service announcement
        void sendServiceRequest()
        {
            Thread.Sleep(2000);
            var udpClient = new UdpClient("239.255.255.250", 1900);

           
            
            byte[] request = Encoding.UTF8.GetBytes("ml-stream-locate");
            
            while (!socketConnected)
            {
                udpClient.Send(request, request.Length);
                Thread.Sleep(3000);
            }
        
            udpClient.Close();
            ssdpRequestThread = null;
        }

        // Listening for service announcement over UDP Multicast containing server address
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


                closeClient();
                client = new TcpClient();
                connectToServerBlocking(address[0],Convert.ToInt32(address[1]));
                receiveStream();
                
                break;
            }

            ssdpListenThread = null;
        }
        
        
    }
