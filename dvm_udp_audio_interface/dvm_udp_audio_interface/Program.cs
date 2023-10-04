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
        isSending = true;
    }
    private void ListenForPackets()
    {
        while (true)
        {
            try
            {
                var endPoint = new IPEndPoint(IPAddress.Parse(config.ReceiveUdp.Address), config.ReceiveUdp.Port);
                byte[] receivedBytes = listener.Receive(ref endPoint);

                uint srcId = (uint)(receivedBytes[receivedBytes.Length - 8] << 24) |
                             (uint)(receivedBytes[receivedBytes.Length - 7] << 16) |
                             (uint)(receivedBytes[receivedBytes.Length - 6] << 8) |
                             (uint)(receivedBytes[receivedBytes.Length - 5]);

                uint dstId = (uint)(receivedBytes[receivedBytes.Length - 4] << 24) |
                             (uint)(receivedBytes[receivedBytes.Length - 3] << 16) |
                             (uint)(receivedBytes[receivedBytes.Length - 2] << 8) |
                             (uint)(receivedBytes[receivedBytes.Length - 1]);

                // Console.WriteLine($"SrcId: {srcId} and DstId: {dstId}");
                if (!isReceivingAudio)
                {
                    Console.WriteLine($"Recieved network call: SRC_ID: {srcId} DST_ID: {dstId}");
                    isReceivingAudio = true;
                }

                inactivityTimer.Stop();
                inactivityTimer.Start();

                byte[] audioData = new byte[receivedBytes.Length - 16];
                Buffer.BlockCopy(receivedBytes, 4, audioData, 0, audioData.Length);

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
        if (isSending)
        {
            byte[] audioPacket = new byte[e.BytesRecorded + 4];

            byte[] srcIdBytes = BitConverter.GetBytes(srcId);
            Array.Reverse(srcIdBytes);
            Buffer.BlockCopy(srcIdBytes, 0, audioPacket, 0, 4);

            Buffer.BlockCopy(e.Buffer, 0, audioPacket, 4, e.BytesRecorded);

            udpClient.Send(audioPacket, audioPacket.Length, sendingEndPoint);
        }
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

            while (true)
            {
                Console.WriteLine("Waitin");
                Console.ReadLine();

                if (handler.isSending)
                {
                    handler.StopCaptureAndSend();
                    Console.WriteLine("Unkeyed");
                }
                else
                {
                    handler.StartCaptureAndSend();
                    Console.WriteLine("Keyed");
                }
            }
        }
    }
}
