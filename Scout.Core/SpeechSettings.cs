namespace Scout.Core;

public sealed record SpeechSettings {
    public bool Enabled {get;init;} = true;
    public string Model {get;init;} = "parakeet-v3";
    public string Device {get;init;} = "cpu";
    public int Threads {get;init;} = 8;
    public int ChunkSeconds {get;init;} = 120;
    public int TimeoutSeconds {get;init;} = 600;
    public void Validate() {
        if(!SpeechModels.Profiles.Any(p=>p.Id==Model))throw new ArgumentException("Choose a supported speech model.");
        if(Device is not ("cpu" or "cuda"))throw new ArgumentException("Choose CPU or NVIDIA GPU for speech transcription.");
        if(Threads<1||Threads>256||ChunkSeconds<30||ChunkSeconds>300||TimeoutSeconds<30||TimeoutSeconds>7200)
            throw new ArgumentException("Speech settings require 1–256 threads, 30–300 second chunks and a 30–7200 second chunk timeout.");
    }
}
