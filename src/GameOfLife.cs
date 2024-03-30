using Godot;
using Godot.Collections;
using Timer = Godot.Timer;

public sealed partial class GameOfLife : Node
{
    [Export]
    private Texture2D _aliveTexture = default!;

    [Export]
    private Texture2D? _binaryDataTexture = default!;

    [Export(PropertyHint.File, "*.glsl")]
    private RDShaderFile _computeShaderFile = default!;

    [Export]
    private Texture2D _deadTexture = default!;

    private bool _firstTick;
    private RDTextureFormat _inputFormat = default!;
    private Image _inputImage = default!;
    private Rid _inputTexture;

    [Export(PropertyHint.Range, "0.0,1.0")]
    private float _noiseFrequency = 0.22F;

    private RDTextureFormat _outputFormat = default!;
    private Image _outputImage = default!;
    private Rid _outputTexture;
    private Rid _pipeline;
    private RenderingDevice _renderingDevice = default!;
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

    public override void _Ready()
    {
        _viewport = GetNodeOrNull<Viewport>("2DViewportContainer/Viewport2D")
                    ?? throw new NullReferenceException("Node 'Viewport2D' not found.");
        _viewportSprite = GetNodeOrNull<Sprite2D>("2DViewportContainer/Viewport2D/ViewportSprite")
                          ?? throw new NullReferenceException("Node 'ViewportSprite' not found.");
        _torus = GetNodeOrNull<MeshInstance3D>("3DViewportContainer/Viewport3D/3D/Torus")
                 ?? throw new NullReferenceException("Node 'Torus' not found.");

        _inputImage = GetInputImage();
        _outputImage = Image.Create(_squareSize, _squareSize, false, Image.Format.L8);
        MergeInputAndOutputImages();
        SetShaderMaterialFromOutputImage();
        SetTorusViewportMaterial();
        _renderingDevice = RenderingServer.CreateLocalRenderingDevice();
        _shader = CreateShader();
        _pipeline = _renderingDevice.ComputePipelineCreate(_shader);
        _inputFormat = GetBaseTextureFormat();
        _outputFormat = GetBaseTextureFormat();
        ComputeBindings();

        _timer = new Timer
        {
            WaitTime = TimeSpan.FromMilliseconds(_updateIntervalMilliseconds).TotalSeconds,
            OneShot = false,
            Autostart = false
        };
        _timer.Timeout += TimerTick;

        AddChild(_timer);
        _firstTick = true;
        _timer.Start();
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

        var computeList = _renderingDevice.ComputeListBegin();

        _renderingDevice.ComputeListBindComputePipeline(computeList, _pipeline);
        _renderingDevice.ComputeListBindUniformSet(computeList, _uniformSet, 0);
        _renderingDevice.ComputeListDispatch(computeList, Groups, Groups, 1);
        _renderingDevice.ComputeListEnd();
        _renderingDevice.Submit();
    }

    private void Render()
    {
        _renderingDevice.Sync();

        var bytes = _renderingDevice.TextureGetData(_outputTexture, 0);
        _renderingDevice.TextureUpdate(_inputTexture, 0, bytes);
        _outputImage.SetData(_squareSize, _squareSize, false, Image.Format.L8, bytes);
        _renderTexture.Update(_outputImage);
    }

    private Image GetInputImage()
    {
        if (_binaryDataTexture is null)
        {
            return GetNoiseImage();
        }

        _squareSize = (int) _binaryDataTexture.GetSize().X;

        return _binaryDataTexture.GetImage();
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

    private void ComputeBindings()
    {
        int[] input = [_squareSize, _squareSize];
        var inputBytes = new byte[input.Length * sizeof(int)];
        Buffer.BlockCopy(input, 0, inputBytes, 0, inputBytes.Length);

        var buffer = _renderingDevice.StorageBufferCreate((uint) inputBytes.Length, inputBytes);
        var uniform = new RDUniform()
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
    }

    private (Rid, RDUniform) CreateTextureAndBindUniform(Image image, RDTextureFormat format, int binding)
    {
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
