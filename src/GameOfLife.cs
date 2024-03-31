using Godot;
using Godot.Collections;
using Timer = Godot.Timer;

public sealed partial class GameOfLife : Node
{
    [Export]
    private Texture2D _aliveTexture = default!;

    [Export(PropertyHint.ResourceType, nameof(SampleTextures))]
    private SampleTextures _binaryDataTextures = default!;

    [Export(PropertyHint.File, "*.glsl")]
    private RDShaderFile _computeShaderFile = default!;

    [Export]
    private Texture2D _deadTexture = default!;

    private bool _firstTick;
    private Rid _buffer;
    private RDTextureFormat _inputFormat = default!;
    private Image _inputImage = default!;
    private Rid _inputTexture;

    [Export(PropertyHint.Range, "0.0,1.0")]
    private float _noiseFrequency = 0.22F;

    private RDTextureFormat _outputFormat = default!;
    private Image _outputImage = default!;
    private Rid _outputTexture;
    private Rid _pipeline;
    private RenderingDevice? _renderingDevice;
    private ImageTexture _renderTexture = default!;
    private Rid _shader;

    [Export(PropertyHint.Range, "8,512")]
    private int _squareSize = 64;

    private Timer? _timer;
    private MeshInstance3D _torus = default!;
    private Rid _uniformSet;

    [Export(PropertyHint.Range, "1,1000")]
    private int _updateIntervalMilliseconds = 200;

    private Viewport _viewport = default!;
    private Sprite2D _viewportSprite = default!;

    public void Configure(Mode mode)
    {
        _timer?.Stop();
        
        if (_renderingDevice is not null)
        {
            FreeRenderingResources(_renderingDevice);
            _renderingDevice = null;
        }
        
        (_squareSize, _inputImage) = GetInputImage(mode);
        _outputImage = Image.Create(_squareSize, _squareSize, false, Image.Format.L8);
        MergeInputAndOutputImages();
        SetShaderMaterialFromOutputImage();
        SetTorusViewportMaterial();
        _renderingDevice = RenderingServer.CreateLocalRenderingDevice();
        _shader = CreateShader();
        _pipeline = _renderingDevice.ComputePipelineCreate(_shader);
        _inputFormat = GetBaseTextureFormat();
        _outputFormat = GetBaseTextureFormat();
        _buffer = ComputeBindings();
        
        _firstTick = true;
        _timer?.Start();
    }

    private void FreeRenderingResources(RenderingDevice device)
    {
        device.FreeRid(_buffer);
        device.FreeRid(_pipeline);
        device.FreeRid(_shader);
        device.FreeRid(_inputTexture);
        device.FreeRid(_outputTexture);
    }

    public override void _Ready()
    {
        _viewport = GetNodeOrNull<Viewport>("2DViewportContainer/Viewport2D")
                    ?? throw new NullReferenceException("Node 'Viewport2D' not found.");
        _viewportSprite = GetNodeOrNull<Sprite2D>("2DViewportContainer/Viewport2D/ViewportSprite")
                          ?? throw new NullReferenceException("Node 'ViewportSprite' not found.");
        _torus = GetNodeOrNull<MeshInstance3D>("3DViewportContainer/Viewport3D/3D/Torus")
                 ?? throw new NullReferenceException("Node 'Torus' not found.");

        _timer = new Timer
        {
            WaitTime = TimeSpan.FromMilliseconds(_updateIntervalMilliseconds).TotalSeconds,
            OneShot = false,
            Autostart = false
        };
        _timer.Timeout += TimerTick;
        AddChild(_timer);
        
        Configure(Mode.Pulsar);
    }

    public override void _ExitTree()
    {
        if (_timer is not null)
        {
            _timer.Timeout -= TimerTick;
        }

        base._ExitTree();
    }

    private void TimerTick()
    {
        CallDeferred(nameof(UpdateGameOfLife));
    }

    private void UpdateGameOfLife()
    {
        if (_firstTick)
        {
            _firstTick = false;
        }
        else
        {
            Render();
        }

        Update();
    }

    private void Update()
    {
        const uint Groups = 32;

        if (_renderingDevice is null)
        {
            return;
        }

        var computeList = _renderingDevice.ComputeListBegin();

        _renderingDevice.ComputeListBindComputePipeline(computeList, _pipeline);
        _renderingDevice.ComputeListBindUniformSet(computeList, _uniformSet, 0);
        _renderingDevice.ComputeListDispatch(computeList, Groups, Groups, 1);
        _renderingDevice.ComputeListEnd();
        _renderingDevice.Submit();
    }

    private void Render()
    {
        if (_renderingDevice is null)
        {
            return;
        }

        _renderingDevice.Sync();

        var bytes = _renderingDevice.TextureGetData(_outputTexture, 0);
        _renderingDevice.TextureUpdate(_inputTexture, 0, bytes);
        _outputImage.SetData(_squareSize, _squareSize, false, Image.Format.L8, bytes);
        _renderTexture.Update(_outputImage);
    }

    private (int squareSize, Image image) GetInputImage(Mode mode)
    {
        return mode switch
               {
                   Mode.Random => (_squareSize, GetNoiseImage()),
                   Mode.Glider => GetData(_binaryDataTextures.GliderDataTexture,
                                          nameof(Mode.Glider)),
                   Mode.GosperGlider => GetData(_binaryDataTextures.GosperGliderDataTexture,
                                                nameof(Mode.GosperGlider)),
                   Mode.Pulsar => GetData(_binaryDataTextures.PulsarDataTexture,
                                          nameof(Mode.Pulsar)),
                   _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
               };

        static (int, Image) GetData(Texture2D? texture, string textureName)
        {
            if (texture is null)
            {
                throw new InvalidOperationException($"Texture {textureName} is not set");
            }

            var size = (int) texture.GetSize().X;
            var image = texture.GetImage();

            return (size, image);
        }
    }

    private Image GetNoiseImage()
    {
        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = _noiseFrequency,
            Seed = Random.Shared.Next()
        };

        return noise.GetImage(_squareSize, _squareSize);
    }

    private void MergeInputAndOutputImages()
    {
        for (var x = 0; x < _squareSize; x++)
        {
            for (var y = 0; y < _squareSize; y++)
            {
                var color = _inputImage.GetPixel(x, y);
                _outputImage.SetPixel(x, y, color);
            }
        }

        _inputImage.SetData(_squareSize, _squareSize, false, Image.Format.L8, _outputImage.GetData());
    }

    private void SetShaderMaterialFromOutputImage()
    {
        if (_viewportSprite.Material is not ShaderMaterial material)
        {
            throw new NullReferenceException("Material is not ShaderMaterial.");
        }

        _renderTexture = ImageTexture.CreateFromImage(_outputImage);

        var cellSize = (int) _aliveTexture.GetSize().X;

        material.SetShaderParameter("deadTexture", _deadTexture);
        material.SetShaderParameter("aliveTexture", _aliveTexture);
        material.SetShaderParameter("binaryDataTexture", _renderTexture);
        material.SetShaderParameter("gridWidth", _squareSize);
        material.SetShaderParameter("cellSize", cellSize);
    }

    private void SetTorusViewportMaterial()
    {
        var material = new StandardMaterial3D
        {
            AlbedoTexture = _viewport.GetTexture()
        };

        _torus.SetSurfaceOverrideMaterial(0, material);
    }

    private Rid ComputeBindings()
    {
        if (_renderingDevice is null)
        {
            return default;
        }

        int[] input = [_squareSize, _squareSize];
        var inputBytes = new byte[input.Length * sizeof(int)];
        Buffer.BlockCopy(input, 0, inputBytes, 0, inputBytes.Length);

        var buffer = _renderingDevice.StorageBufferCreate((uint) inputBytes.Length, inputBytes);
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 0
        };
        uniform.AddId(buffer);

        (_inputTexture, var inputUniform) = CreateTextureAndBindUniform(_inputImage, _inputFormat, 1);
        (_outputTexture, var outputUniform) = CreateTextureAndBindUniform(_outputImage, _outputFormat, 2);

        IEnumerable<RDUniform> bindings = [uniform, inputUniform, outputUniform];

        _uniformSet = _renderingDevice.UniformSetCreate(new Array<RDUniform>(bindings),
                                                        _shader, 0);

        return buffer;
    }

    private (Rid, RDUniform) CreateTextureAndBindUniform(Image image, RDTextureFormat format, int binding)
    {
        if (_renderingDevice is null)
        {
            throw new InvalidOperationException("Rendering device is not set");
        }

        var view = new RDTextureView();
        Array<byte[]> data = [image.GetData()];
        var texture = _renderingDevice.TextureCreate(format, view, data);
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = binding
        };
        uniform.AddId(texture);

        return (texture, uniform);
    }

    private Rid CreateShader()
    {
        if (_renderingDevice is null)
        {
            throw new InvalidOperationException("Rendering device is not set");
        }

        var spirV = _computeShaderFile.GetSpirV();

        return _renderingDevice.ShaderCreateFromSpirV(spirV);
    }

    private RDTextureFormat GetBaseTextureFormat() =>
        new()
        {
            Width = (uint) _squareSize,
            Height = (uint) _squareSize,
            Format = RenderingDevice.DataFormat.R8Unorm,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit
                        | RenderingDevice.TextureUsageBits.CanUpdateBit
                        | RenderingDevice.TextureUsageBits.CanCopyFromBit
        };
}

public enum Mode
{
    Glider,
    GosperGlider,
    Pulsar,
    Random
}
