namespace SimulationCrew.AIBridge.Audio.Codecs
{
    /// <summary>
    /// Simple packet structure for OGG/Opus data
    /// </summary>
    public class OggPacket
    {
        public byte[] Data { get; set; }
        public bool IsHeader { get; set; }
    }
}