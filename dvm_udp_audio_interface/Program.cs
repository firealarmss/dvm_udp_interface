using System;
using System.Net;
using System.IO;
using System.Threading.Tasks;
using System.Net.Sockets;
using YamlDotNet.Serialization;
using NAudio.Wave;

public class AppConfig
{
    public uint SrcId { get; set; }
    public bool MetaData { get; set; }
    public uint DstId { get; set; }
    public bool Vox { get; set; }

    public UdpConfig ReceiveUdp { get; set; }
    public UdpConfig SendUdp { get; set; }
}

public class UdpConfig
{
    public string Address { get; set; }
    public int Port { get; set; }
}
public class UdpAudioSender : IDisposable
{
    private bool connectedToRouter = false;
    private readonly IPEndPoint sendingEndPoint;
    private readonly UdpClient udpClient;
    private UdpClient listener;
    private WaveInEvent waveIn;
    private IWavePlayer waveOut;
    private BufferedWaveProvider waveProvider;
    private bool isReceivingAudio = false;
    private System.Timers.Timer inactivityTimer;
    private bool isSending = false;
    private readonly AppConfig config;

    private uint srcId;
    private bool MetaData;
    private uint dstId;
    private bool vox;
    private const float VoiceActivationThreshold = 0.05f;

    private AppConfig LoadConfig(string path)
    {
        var deserializer = new DeserializerBuilder().Build();
        var yaml = File.ReadAllText(path);
        return deserializer.Deserialize<AppConfig>(yaml);
    }

    public UdpAudioSender(string configPath)
    {
        config = LoadConfig(configPath);
        srcId = config.SrcId;
        dstId = config.DstId;
        vox = config.Vox;

        MetaData = config.MetaData;
        udpClient = new UdpClient();
        sendingEndPoint = new IPEndPoint(IPAddress.Parse(config.SendUdp.Address), config.SendUdp.Port);
    }
    public void StartListening()
    {
        listener = new UdpClient(config.ReceiveUdp.Port);
        waveProvider = new BufferedWaveProvider(new WaveFormat(8000, 16, 1));
        waveOut = new WaveOutEvent();
        waveOut.Init(waveProvider);
        waveOut.Play();
        inactivityTimer = new System.Timers.Timer(1000);
        inactivityTimer.Elapsed += InactivityTimer_Elapsed;
        inactivityTimer.AutoReset = false;

        Task.Run(() => ListenForPackets());
    }
    public void StartCaptureAndSend()
    {
        if (waveIn != null)
        {
            waveIn.StopRecording();
            waveIn.Dispose();
            waveIn = null;
        }
        waveIn = new WaveInEvent();
        waveIn.BufferMilliseconds = 20;
        waveIn.NumberOfBuffers = 2;
        waveIn.DataAvailable += WaveIn_DataAvailable;
        waveIn.WaveFormat = new WaveFormat(8000, 16, 1);
        waveIn.StartRecording();
        if (!vox)
        {
            isSending = true;
        }
    }
    private void ListenForPackets()
    {
        while (true)
        {
            try
            {
                var endPoint = new IPEndPoint(IPAddress.Parse(config.ReceiveUdp.Address), config.ReceiveUdp.Port);
                byte[] receivedBytes = listener.Receive(ref endPoint);

                int expectedLength = connectedToRouter ? 324 : 328;

                /*                if (receivedBytes.Length < expectedLength || receivedBytes.Length == 320)
                                {
                                    Console.WriteLine($"Received unexpected data {receivedBytes.Length}");
                                    continue;
                                }*/
                Console.WriteLine(receivedBytes.Length);
                int srcId = (receivedBytes[0] << 24) |
                            (receivedBytes[1] << 16) |
                            (receivedBytes[2] << 8) |
                            receivedBytes[3];

                int audioDataStartIndex = 4;
                int audioDataLength = connectedToRouter ? 316 : 320;

                int dstId = 0;
                if (!connectedToRouter)
                {
                    dstId = (receivedBytes[7] << 24) |
                            (receivedBytes[6] << 16) |
                            (receivedBytes[5] << 8) |
                            receivedBytes[4];
                    audioDataStartIndex = 8;
                }

                if (!MetaData)
                {
                    audioDataStartIndex = 0;
                    srcId = 0;
                    dstId = 0;
                }

                Console.WriteLine($"Received network call: srcId: {srcId}, dstId: {dstId}");
                inactivityTimer.Stop();
                inactivityTimer.Start();

                byte[] audioData = new byte[audioDataLength];
                Buffer.BlockCopy(receivedBytes, audioDataStartIndex, audioData, 0, audioDataLength);

                waveProvider.AddSamples(audioData, 0, audioData.Length);
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

    private void WaveIn_DataAvailable(object sender, WaveInEventArgs e)
    {
        //Console.WriteLine(isSending);
        if (vox)
        {
            float volume = CalculateVolume(e.Buffer, e.BytesRecorded);
            if (volume > VoiceActivationThreshold)
            {
                isSending = true;
            }
            else
            {
                isSending = false;
            }
         //   Console.WriteLine($"Volume: {volume}");
        }
        if (isSending)
        {
            byte[] audioDataToSend = new byte[320];

            Buffer.BlockCopy(e.Buffer, 0, audioDataToSend, 0, Math.Min(e.BytesRecorded, 320));

            byte[] audioPacket;

            if (MetaData)
            {
                audioPacket = new byte[328];

                Buffer.BlockCopy(audioDataToSend, 0, audioPacket, 0, 320);

                byte[] srcIdBytes = BitConverter.GetBytes(srcId);
                Array.Reverse(srcIdBytes);
                Buffer.BlockCopy(srcIdBytes, 0, audioPacket, 320, 4);

                byte[] dstIdBytes = BitConverter.GetBytes(connectedToRouter ? 0 : dstId);
                Array.Reverse(dstIdBytes);
                Buffer.BlockCopy(dstIdBytes, 0, audioPacket, 324, 4);
            }
            else
            {
                audioPacket = audioDataToSend;
            }

            udpClient.Send(audioPacket, audioPacket.Length, sendingEndPoint);
        }
    }

    private float CalculateVolume(byte[] buffer, int bytesRecorded)
    {
        float max = 0;
        for (int index = 0; index < bytesRecorded; index += 2)
        {
            short sample = (short)((buffer[index + 1] << 8) | buffer[index]);
            float sample32 = sample / 32768.0f;
            if (sample32 < 0) sample32 = -sample32;
            if (sample32 > max) max = sample32;
        }
        return max;
    }

    public void StopCaptureAndSend()
    {
        isSending = false;
    }

    public void StopListening()
    {
        listener?.Close();
        waveOut?.Stop();
        waveOut?.Dispose();
        waveProvider?.ClearBuffer();
    }
    private void InactivityTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
        if (isReceivingAudio)
        {
            Console.WriteLine("End call");
            isReceivingAudio = false;
        }
    }
    public void Dispose()
    {
        StopCaptureAndSend();
        StopListening();

        udpClient?.Close();
        udpClient?.Dispose();

        listener?.Close();

        waveIn?.StopRecording();
        waveIn?.Dispose();

        waveOut?.Stop();
        waveOut?.Dispose();
    }

    public static void Main()
    {
        using (UdpAudioSender handler = new UdpAudioSender("config.yml"))
        {
            handler.StartListening();
            handler.StartCaptureAndSend(); // Start capturing immediately for VOX mode

            while (true)
            {
                if (!handler.config.Vox)
                {
                    Console.WriteLine("Press Enter to start/stop sending audio.");
                    Console.ReadLine();

                    if (handler.isSending)
                    {
                        handler.StopCaptureAndSend();
                        Console.WriteLine("Stopped Sending");
                    }
                    else
                    {
                        handler.StartCaptureAndSend();
                        Console.WriteLine("Started Sending");
                    }
                }
                else
                {
                    Console.WriteLine("Voice activated mode. Speak to send audio.");
                    Task.Delay(5000).Wait();
                }
            }
        }
    }
}
