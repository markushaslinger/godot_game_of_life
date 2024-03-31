using Godot;

[GlobalClass]
public sealed partial class SampleTextures : Resource
{
    public SampleTextures()
    {
        GliderDataTexture = null;
        GosperGliderDataTexture = null;
        PulsarDataTexture = null;
    }
    
    [Export]
    public Texture2D? GliderDataTexture { get; set; }

    [Export]
    public Texture2D? GosperGliderDataTexture { get; set; }

    [Export]
    public Texture2D? PulsarDataTexture { get; set; }
}
