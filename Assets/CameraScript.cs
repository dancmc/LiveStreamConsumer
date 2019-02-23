using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using UnityEngine.XR.MagicLeap;
using System.Collections;
using System.Net.Sockets;
using System.Net;
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AOT;
using UnityEngine.Experimental.Rendering;
using System.Threading;
using System.Collections.Concurrent;


public class CameraScript : MonoBehaviour
{
    TcpClient client = new TcpClient();

//	[DllImport("CameraLib")]
//	private static extern void MLog(String str);

    private static void MLog(String str)
    {
        Debug.unityLogger.Log(str);
    }

    private static BlockingCollection<int> sendingQueue = new BlockingCollection<int>();
    private static bool texture1Available = true;
    private static bool texture2Available = true;

    private static CameraScript instance;
    public Camera renderCamera;
    private Camera gameCamera;
    
    public RawImage rawImage;
    private Texture2D rawImageTexture;

    private Thread sendThread;
    private Thread listenThread;
    private bool editorMode = true;
    private static bool captureActive;
    private volatile bool socketConnected;

    private static Texture2D videoCaptureTexture;
    private static Texture2D videoCaptureTexture2;
    private RenderTexture testRt;
    private RenderTexture renderRt;


    private bool readyToSend = true;
    public Shader blendShader;

    private Material material;

    private int count = 0;
    private VideoState videostate = VideoState.VIDEO_NONE;
    String filepath;


    public CameraScript()
    {
        instance = this;
        Debug.unityLogger.logEnabled = true;
    }


    // Use this for initialization
    IEnumerator Start()
    {
        material = new Material(blendShader);


        videoCaptureTexture = new Texture2D(960, 540, TextureFormat.RGBA32, false);
        videoCaptureTexture2 = new Texture2D(960, 540, TextureFormat.RGBA32, false);
        testRt = new RenderTexture(960, 540, 0);
        renderRt = new RenderTexture(960, 540, 0);


        var previous = RenderTexture.active;
        RenderTexture.active = testRt;
        var color = new Color(0f, 0f, 0f, 1f);
        GL.Clear(true, true, color);
        RenderTexture.active = previous;
//		
        renderCamera.targetTexture = renderRt;
        material.SetTexture("_Overlay", renderRt);


        yield return StartCoroutine(nameof(RenderAndSend));
        yield return null;
    }

    private void Awake()
    {
        
        listenThread = new Thread(listenForServiceReply);
        listenThread.IsBackground = true;
        listenThread.Start();

        sendThread = new Thread(sendServiceRequest);
        sendThread.IsBackground = true;
        sendThread.Start();


        filepath = Path.Combine(Application.persistentDataPath, "vid.mp4");

        gameCamera = Camera.main;
        rawImageTexture = new Texture2D(960, 540, TextureFormat.RGB24, false);
        rawImage.texture = rawImageTexture;
    }

    private long currentTime()
    {
        return (long) (DateTime.Now - new DateTime(1970, 1, 1)).TotalMilliseconds;
    }

    public static void setCaptureActive(bool active)
    {
        if (instance != null)
        {
            if (active)
            {
                MLog("Capture Activated");

                MLCamera.StartPreview();
                captureActive = true;
                instance.videostate = VideoState.VIDEO_READY;
            }
            else
            {
                MLCamera.StopPreview();
                MLCamera.Disconnect();
                MLCamera.Stop();
                captureActive = false;
            }
        }
    }

    private void OnPreRender()
    {
        renderCamera.gameObject.transform.position = gameCamera.gameObject.transform.position;
        renderCamera.gameObject.transform.rotation = gameCamera.gameObject.transform.rotation;
    }


    IEnumerator RenderAndSend()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();

//			if (videostate == VideoState.VIDEO_READY)
//			{
//				videostate = VideoState.VIDEO_STARTED;
//				MLog("Starting video");
//				MLCamera.StartVideoCapture(filepath);
//				MLog("Started video");
//				count = 0;
//			}
//			count++;
//
//			if (count > 240 && videostate == VideoState.VIDEO_STARTED)
//			{
//				videostate = VideoState.VIDEO_ENDED;
//				MLog("Stopping video");
//				MLCamera.StopVideoCapture();
//				MLog("Stopped video");
//				
//			}
//
//			if (videostate == VideoState.VIDEO_ENDED)
//			{
//				sendVideo();
//			}

            long t = currentTime();
            if (socketConnected && (captureActive||editorMode) && readyToSend)
            {
                readyToSend = false;

                //  Create a temporary RenderTexture of the same size as the texture
                var tmp = RenderTexture.GetTemporary(
                    960,
                    540,
                    0,
                    RenderTextureFormat.Default,
                    RenderTextureReadWrite.Linear);

                MLog("Blitting " + (currentTime() - t));


//				Graphics.Blit(MLCamera.PreviewTexture2D, tmp,material);
                Graphics.Blit(testRt, tmp, material);


                var previous = RenderTexture.active;
                RenderTexture.active = tmp;
                MLog("Reading Pixels " + (currentTime() - t));


                videoCaptureTexture.ReadPixels(new Rect(0, 0, 960, 540), 0,
                    0);
                videoCaptureTexture.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(tmp);

                var jpgBytes = videoCaptureTexture.EncodeToJPG(40);

                rawImageTexture.LoadImage(jpgBytes);
                    
                var bytes = Encoding.UTF8.GetBytes(jpgBytes.Length + "\n");
                MLog("Starting Sending " + jpgBytes.Length);
                try
                {
                    instance.client.GetStream().Write(bytes, 0, bytes.Length);
                    instance.client.GetStream().Write(jpgBytes, 0, jpgBytes.Length);
                }
                catch (Exception e)
                {
                    MLog(e.Message);
                    socketConnected = false;
                }

                MLog("Done " + jpgBytes.Length);


                readyToSend = true;
            }
        }
    }

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
            client.Close();
            client = new TcpClient();
            client.Connect(address[0], Convert.ToInt32(address[1]));
            var initialMessage = Encoding.UTF8.GetBytes("producer\n");
            client.GetStream().Write(initialMessage,0, initialMessage.Length);
            var reader = new StreamReader(client.GetStream());
            MLog(reader.ReadLine());
            
            socketConnected = true;
            break;
        }
    }


    void sendVideo()
    {
        if (File.Exists(filepath) && new FileInfo(filepath).Length > 1600000)
        {
            videostate = VideoState.VIDEO_SENT;
            MLog("Sending video");


            byte[] videoBytes = File.ReadAllBytes(filepath);

            MLog("Starting Sending " + videoBytes.Length);
            byte[] bytes = Encoding.UTF8.GetBytes(videoBytes.Length + "\n");
            instance.client.GetStream().Write(bytes, 0, bytes.Length);
            instance.client.GetStream().Write(videoBytes, 0, videoBytes.Length);
            MLog("Done Sending" + videoBytes.Length);
        }
        else
        {
            MLog("File does not exist yet");
        }
    }


    enum VideoState
    {
        VIDEO_NONE,
        VIDEO_READY,
        VIDEO_STARTED,
        VIDEO_ENDED,
        VIDEO_SENT
    }


//	public static void SendingThread()
//	{
//		
//		// this should go in the coroutine
//		Texture2D texture;
//		int textureID;
//		if (texture1Available)
//		{
//			texture = videoCaptureTexture;
//			texture1Available = false;
//			textureID = 1;
//		} else if(texture2Available)
//
//		{
//			texture = videoCaptureTexture2;
//			texture2Available = false;
//			textureID = 2;
//		}
//		else
//		{
//			continue;
//		}
//		
//		
//		
//		foreach (int id in sendingQueue.GetConsumingEnumerable())
//		{
//			MLog("Encoding");
//
//			byte[] jpgBytes = null;
//			if (id==1)
//				
//			{
//				jpgBytes = videoCaptureTexture.EncodeToJPG(40);
//				texture1Available = true;
//			} else if(id==2)
//			{
//				jpgBytes = videoCaptureTexture2.EncodeToJPG(40);
//				texture2Available = true;
//			}
//			
//
//			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(jpgBytes.Length + "\n");
//			MLog("Starting Sending " + jpgBytes.Length);
//			instance.client.GetStream().Write(bytes, 0, bytes.Length);
//			instance.client.GetStream().Write(jpgBytes, 0, jpgBytes.Length);
//			MLog("Done " + jpgBytes.Length);
//		}
//	}
}