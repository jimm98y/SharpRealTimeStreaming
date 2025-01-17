using Haukcode.PcapngUtils;
using Haukcode.PcapngUtils.Common;
using Haukcode.PcapngUtils.PcapNG.BlockTypes;
using Microsoft.Extensions.Configuration;
using SharpRTSPServer;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
string hostName = config["HostName"];
ushort port = ushort.Parse(config["Port"]);
string userName = config["UserName"];
string password = config["Password"];
string filePath = config["FilePath"];

long lastVideoMessageTime = -1;
long lastAudioMessageTime = -1;
long lastVideoRealTime = -1;
long lastAudioRealTime = -1;

bool _readNext = false;
Stopwatch _stopwatch = new Stopwatch();
ProxyTrack videoTrack = null;
ProxyTrack audioTrack = null;
var rtspProtocolParser = new RtspProtocolParser();

var sdpTask = Task.Run(() =>
{
    while (true)
    {
        var reader = IReaderFactory.GetReader(filePath);
        _stopwatch.Stop();
        lastVideoMessageTime = -1;
        lastAudioMessageTime = -1;
        lastVideoRealTime = -1;
        lastAudioRealTime = -1;
        _stopwatch.Start();
        reader.OnReadPacketEvent += Reader_OnReadPacketEvent;
        reader.ReadPackets(CancellationToken.None); // TODO timing
        reader.OnReadPacketEvent -= Reader_OnReadPacketEvent;
    }
});

using (var server = new RTSPServer(port, userName, password))
{
    await rtspProtocolParser.Sempahore.WaitAsync();

    server.OverrideSDP(rtspProtocolParser.SDP, true);

    videoTrack = new ProxyTrack(TrackType.Video);
    server.AddVideoTrack(videoTrack);

    audioTrack = new ProxyTrack(TrackType.Audio);
    server.AddAudioTrack(audioTrack);

    videoTrack?.Start();
    audioTrack?.Start();

    try
    {
        server.StartListen();
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.ToString());
    }

    Console.WriteLine($"RTSP URL is rtsp://{userName}:{password}@{hostName}:{port}");

    Console.WriteLine("Press any key to exit");
    while (!Console.KeyAvailable)
    {
        Thread.Sleep(250);
    }
}

void Reader_OnReadPacketEvent(object context, IPacket packet)
{
    if (packet is EnhancedPacketBlock enhanced)
    {
        var ipHeader = ParseIPHeader(packet);

        if (ipHeader.Protocol == 6) // TCP - RTSP
        {
            var tcpHeader = ParseTCPHeader(packet, 4 + ipHeader.HeaderLength);
            Debug.WriteLine($"Source: {ipHeader.SourceIP}:{tcpHeader.SourcePort}, Dest: {ipHeader.DestintationIP}:{tcpHeader.DestinationPort}, Ver: {ipHeader.Version}");

            if (tcpHeader.Flags == 0x010) // ACK
                return;

            ParseData(packet.Data.Skip(4 + ipHeader.HeaderLength + tcpHeader.TcpHeaderLength).ToArray(), tcpHeader, packet.Seconds, packet.Microseconds);
        }
        else if (ipHeader.Protocol == 17) // UDP
        {
            var udpHeader = ParseUDPHeader(packet, 4 + ipHeader.HeaderLength);
            Debug.WriteLine($"Source: {ipHeader.SourceIP}:{udpHeader.SourcePort}, Dest: {ipHeader.DestintationIP}:{udpHeader.DestinationPort}, Ver: {ipHeader.Version}");

            ParseData(packet.Data.Skip(4 + ipHeader.HeaderLength + 8).ToArray(), udpHeader, packet.Seconds, packet.Microseconds);
        }
    }
}

void ParseData(byte[] data, object header, uint seconds, uint microseconds)
{
    UDPHeader udp = header as UDPHeader;

    if (udp != null && data.Length > 1) // TODO
    {
        if (data[0] == 0x80 && data[1] != 0xc8) // 0xc8 sender report -> ignore rtcp
        {
            long messageTime = seconds * 1000 + (microseconds / 1000);
            long realTime = (uint)_stopwatch.ElapsedMilliseconds;

            if (rtspProtocolParser.Ports[0].Contains(udp.SourcePort) && rtspProtocolParser.Ports[0].Contains(udp.DestinationPort))
            {
                if (lastVideoMessageTime == -1)
                    lastVideoMessageTime = messageTime;

                if (lastVideoRealTime == -1)
                    lastVideoRealTime = realTime;

                int sleep = (int)((messageTime - lastVideoMessageTime) - (realTime - lastVideoRealTime));
                Debug.WriteLine($"Video sleep {realTime - lastVideoRealTime}; {messageTime - lastVideoMessageTime}; sleep {sleep}");
                if (sleep > 0)
                {
                    Thread.Sleep(sleep);
                }
                videoTrack?.FeedInRawSamples(RTPPacketUtil.ReadTS(data), new ReadOnlySequence<byte>(data));
            }
            else if (rtspProtocolParser.Ports.Count > 1 && rtspProtocolParser.Ports[1].Contains(udp.SourcePort) && rtspProtocolParser.Ports[1].Contains(udp.DestinationPort))
            {
                if (lastAudioMessageTime == -1)
                    lastAudioMessageTime = messageTime;

                if (lastAudioRealTime == -1)
                    lastAudioRealTime = realTime;

                int sleep = (int)((messageTime - lastAudioMessageTime) - (realTime - lastAudioRealTime));
                Debug.WriteLine($"Audio sleep {realTime - lastAudioRealTime}; {messageTime - lastAudioMessageTime}; sleep {sleep}");
                if (sleep > 0)
                {
                    Thread.Sleep(sleep);
                }

                audioTrack?.FeedInRawSamples(RTPPacketUtil.ReadTS(data), new ReadOnlySequence<byte>(data));
            }
        }
    }
    else
    {
        using (MemoryStream ms = new MemoryStream(data))
        {
            using (StreamReader strr = new StreamReader(ms))
            {
                string firstLine = strr.ReadLine();
                if (firstLine != null && firstLine.Contains("RTSP/") || _readNext)
                {
                    if (_readNext) _readNext = false;

                    // RTSP
                    _readNext = rtspProtocolParser.Parse(Encoding.UTF8.GetString(data));
                }
            }
        }
    }
}

static UDPHeader ParseUDPHeader(IPacket packet, int headerLength)
{
    ushort sourcePort = (ushort)(packet.Data[headerLength] << 8 | packet.Data[headerLength + 1]);
    ushort destinationPort = (ushort)(packet.Data[headerLength + 2] << 8 | packet.Data[headerLength + 2 + 1]);
    ushort length = (ushort)(packet.Data[headerLength + 4] << 8 | packet.Data[headerLength + 4 + 1]);
    ushort checksum = (ushort)(packet.Data[headerLength + 6] << 8 | packet.Data[headerLength + 6 + 1]);
    return new UDPHeader(
        sourcePort,
        destinationPort,
        length,
        checksum);
}

static TCPHeader ParseTCPHeader(IPacket packet, int headerLength)
{
    ushort sourcePort = (ushort)(packet.Data[headerLength] << 8 | packet.Data[headerLength + 1]);
    ushort destinationPort = (ushort)(packet.Data[headerLength + 2] << 8 | packet.Data[headerLength + 2 + 1]);
    uint sequenceNumber = (uint)(packet.Data[headerLength + 4] << 24 | packet.Data[headerLength + 5] << 16 | packet.Data[headerLength + 6] << 8 | packet.Data[headerLength + 7]);
    uint acknowledgementNumber = (uint)(packet.Data[headerLength + 8] << 24 | packet.Data[headerLength + 9] << 16 | packet.Data[headerLength + 10] << 8 | packet.Data[headerLength + 11]);

    int tcpHeaderLength = (packet.Data[headerLength + 12] >> 4) * 4;
    int flags = (packet.Data[headerLength + 12] & 0x0F) << 8 | (packet.Data[headerLength + 13]);

    ushort window = (ushort)(packet.Data[headerLength + 14] << 8 | packet.Data[headerLength + 15]);
    ushort checksum = (ushort)(packet.Data[headerLength + 16] << 8 | packet.Data[headerLength + 17]);
    ushort urgentPointer = (ushort)(packet.Data[headerLength + 18] << 8 | packet.Data[headerLength + 19]);

    return new TCPHeader(
        sourcePort,
        destinationPort,
        sequenceNumber,
        acknowledgementNumber,
        tcpHeaderLength,
        flags,
        window,
        checksum,
        urgentPointer);
}

static IPHeader ParseIPHeader(IPacket packet)
{
    // TODO VLAN
    int family = BitConverter.ToInt32(packet.Data, 0);
    int version = packet.Data[4] >> 4;
    int headerLength = (packet.Data[4] & 0x0F) * 4;

    byte differentiatedServicesField = packet.Data[5];
    ushort totalLength = (ushort)(packet.Data[6] << 8 | packet.Data[7]);
    ushort identification = (ushort)(packet.Data[8] << 8 | packet.Data[9]);
    byte flags = (byte)(packet.Data[10] >> 5);
    ushort fragmentOffset = (ushort)((packet.Data[10] & 0x1f) << 8 | packet.Data[11]);
    byte ttl = packet.Data[12];
    byte protocol = packet.Data[13];
    ushort headerCheckSum = (ushort)(packet.Data[14] << 8 | packet.Data[15]);

    int ipaddrLen = version == 4 ? 4 : 16;
    IPAddress sourceIP = new IPAddress(packet.Data.Skip(16).Take(ipaddrLen).ToArray());
    IPAddress destintationIP = new IPAddress(packet.Data.Skip(16 + ipaddrLen).Take(ipaddrLen).ToArray());

    var ipHeader = new IPHeader(
        family,
        version,
        headerLength,
        differentiatedServicesField,
        totalLength,
        identification,
        flags,
        fragmentOffset,
        ttl,
        protocol,
        headerCheckSum,
        sourceIP,
        destintationIP);
    return ipHeader;
}

public class RtspProtocolParser
{
    public SemaphoreSlim Sempahore = new SemaphoreSlim(0);
    public string SDP { get; set; }

    public List<List<int>> Ports { get; set; } = new List<List<int>>();

    private int _lastSetup = -1;

    public bool Parse(string rtsp)
    {
        Debug.WriteLine(rtsp);

        if (rtsp.StartsWith("OPTIONS"))
        {

        }
        else if (rtsp.StartsWith("DESCRIBE"))
        {

        }
        else if (rtsp.StartsWith("SETUP"))
        {
            string[] setupParts = rtsp.Split(' ');
            _lastSetup++;
        }
        else if (rtsp.StartsWith("v="))
        {
            SDP = rtsp;
            Sempahore.Release();
        }
        else if (rtsp.StartsWith("RTSP"))
        {
            // response
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(rtsp)))
            {
                using (StreamReader strr = new StreamReader(ms))
                {
                    string line = null;
                    string contentType = null;
                    int contentLength = -1;

                    while ((line = strr.ReadLine()) != null)
                    {
                        const string CONTENT_TYPE = "Content-Type:";
                        const string CONTENT_LENGTH = "Content-Length:";
                        const string TRANSPORT = "Transport:";
                        const string CLIENT_PORT = "client_port=";
                        const string SERVER_PORT = "server_port=";
                        if (line.StartsWith(CONTENT_TYPE))
                        {
                            contentType = line.Substring(CONTENT_TYPE.Length).Trim();
                        }
                        else if (line.StartsWith(CONTENT_LENGTH))
                        {
                            int.TryParse(line.Substring(CONTENT_LENGTH.Length).Trim(), out contentLength);
                        }
                        else if (line.StartsWith(TRANSPORT)) // SETUP response
                        {
                            int[] clientPorts = null;
                            int[] serverPorts = null;
                            string[] split = line.Substring(TRANSPORT.Length).Trim().Split(';');
                            foreach (var s in split)
                            {
                                string str = s.Trim();
                                if (str.StartsWith(CLIENT_PORT))
                                {
                                    clientPorts = str.Substring(CLIENT_PORT.Length).Split('-').Select(int.Parse).ToArray();
                                }
                                else if (str.StartsWith(SERVER_PORT))
                                {
                                    serverPorts = str.Substring(SERVER_PORT.Length).Split('-').Select(int.Parse).ToArray();
                                }
                            }

                            if (_lastSetup >= 0 && serverPorts != null && clientPorts != null)
                            {
                                this.Ports.Add(new List<int>());
                                this.Ports[_lastSetup].Add(clientPorts[0]);
                                this.Ports[_lastSetup].Add(clientPorts[1]);
                                this.Ports[_lastSetup].Add(serverPorts[0]);
                                this.Ports[_lastSetup].Add(serverPorts[1]);
                            }
                        }
                    }

                    if (ms.Position == ms.Length && contentLength > (ms.Length - ms.Position))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

public class UDPHeader
{
    public UDPHeader(ushort sourcePort, ushort destinationPort, ushort length, ushort checksum)
    {
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        Length = length;
        Checksum = checksum;
    }

    public ushort SourcePort { get; }
    public ushort DestinationPort { get; }
    public ushort Length { get; }
    public ushort Checksum { get; }
}

public class TCPHeader
{
    public TCPHeader(ushort sourcePort, ushort destinationPort, uint sequenceNumber, uint acknowledgementNumber, int tcpHeaderLength, int flags, ushort window, ushort checksum, ushort urgentPointer)
    {
        SourcePort = sourcePort;
        DestinationPort = destinationPort;
        SequenceNumber = sequenceNumber;
        AcknowledgementNumber = acknowledgementNumber;
        TcpHeaderLength = tcpHeaderLength;
        Flags = flags;
        Window = window;
        Checksum = checksum;
        UrgentPointer = urgentPointer;
    }

    public ushort SourcePort { get; }
    public ushort DestinationPort { get; }
    public uint SequenceNumber { get; }
    public uint AcknowledgementNumber { get; }
    public int TcpHeaderLength { get; }
    public int Flags { get; }
    public ushort Window { get; }
    public ushort Checksum { get; }
    public ushort UrgentPointer { get; }
}

public class IPHeader
{
    public IPHeader(
        int family,
        int version,
        int headerLength,
        byte differentiatedServicesField,
        ushort totalLength,
        ushort identification,
        byte flags,
        ushort fragmentOffset,
        byte ttl,
        byte protocol,
        ushort headerCheckSum,
        IPAddress sourceIP,
        IPAddress destintationIP)
    {
        Family = family;
        Version = version;
        HeaderLength = headerLength;
        DifferentiatedServicesField = differentiatedServicesField;
        TotalLength = totalLength;
        Identification = identification;
        Flags = flags;
        FragmentOffset = fragmentOffset;
        Ttl = ttl;
        Protocol = protocol;
        HeaderCheckSum = headerCheckSum;
        SourceIP = sourceIP;
        DestintationIP = destintationIP;
    }

    public int Family { get; }
    public int Version { get; }
    public int HeaderLength { get; }
    public byte DifferentiatedServicesField { get; }
    public ushort TotalLength { get; }
    public ushort Identification { get; }
    public byte Flags { get; }
    public ushort FragmentOffset { get; }
    public byte Ttl { get; }
    public byte Protocol { get; }
    public ushort HeaderCheckSum { get; }
    public IPAddress SourceIP { get; }
    public IPAddress DestintationIP { get; }
}