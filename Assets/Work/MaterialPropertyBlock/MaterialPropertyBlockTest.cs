using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// <b>MaterialPropertyBlock 实测</b>（内置渲染管线 · Unity 2022.3）。
///
/// <para>
/// 用 8 个对照场景，把"同材质 + 每个物体不同属性 + 到底能不能合并 DrawCall"这件事量出来：
/// 屏幕上实时显示 Profiler 的渲染计数器 Batches / DrawCalls / SetPassCalls（30 帧平均），
/// 切换场景时还会往 Console 打一行结果，方便横向对比。
/// </para>
/// <para>
/// 注意：与 <c>UnityStats</c> 不同，这里用的是公开 API <see cref="ProfilerRecorder"/> +
/// <see cref="ProfilerCategory.Render"/>，编辑器与真机都能读；计数器名不存在时显示 n/a。
/// </para>
///
/// <para><b>按键</b>：<c>1</c>~<c>9</c> 切场景 1~9 · <c>0</c> 切场景 10 · <c>空格</c> 隐藏全部物体看空场景基线 · <c>R</c> 重新随机颜色。
/// 所有场景的物体数都是同一个常量 <see cref="ObjectCount"/>，保证横向对比时唯一的变量是"写法"。</para>
///
/// <para><b>怎么读结果</b>：Batches/DrawCalls ≈ 物体数 表示每个物体一次 draw（没合批）；
/// ≈ 1~2 表示全部合并（合批成立）；介于两者之间通常是"按批大小分块"（例如 GPU 实例化的实例数上限）。
/// 想看原因就打开 <c>Window &gt; Analysis &gt; Frame Debugger</c>：选中某次 draw，它会直接告诉你
/// 为什么没有和上一次合并（例如 "Objects have different MaterialPropertyBlock set."）。</para>
///
/// <para><b>本测试基于的用法约定</b>：只复用一个 <see cref="MaterialPropertyBlock"/> 实例，
/// 每画一个物体前 Clear → SetXXX → <c>renderer.SetPropertyBlock(block)</c>（值在 SetPropertyBlock 时被拷贝，
/// 所以复用是安全的，也是 Unity 官方推荐的高效写法）。</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class MaterialPropertyBlockTest : MonoBehaviour
{
    /// <summary>测试用着色器（同目录的 MaterialPropertyBlockTest.shader）；留空则尝试 Shader.Find 兜底。</summary>
    public Shader testShader;

    // ================= 静态配置 =================

    /// <summary>每个场景固定用这么多物体（常量：所有场景一致，横向对比才干净）。</summary>
    private const int ObjectCount = 100;

    private const int StatWindow = 30;   // 统计平均的帧数

    private static readonly string[] ScenarioTitles =
    {
        "SpriteRenderer · 同材质 + 材质 Instancing OFF · 不打 MaterialPropertyBlock（基线）",
        "SpriteRenderer · 同材质 + 材质 Instancing OFF · 每物体不同 MaterialPropertyBlock  ★核心",
        "SpriteRenderer · 同材质 + 材质 Instancing ON · 不打 MaterialPropertyBlock",
        "SpriteRenderer · 同材质 + 材质 Instancing ON · 每物体不同 MaterialPropertyBlock  ★",
        "SpriteRenderer · 每物体 new 一个 Material（对照）",
        "MeshRenderer(Quad) · 同材质 · 不打 MaterialPropertyBlock（基线）",
        "MeshRenderer(Quad) · 同材质 · 每物体不同 MaterialPropertyBlock · Instancing OFF",
        "MeshRenderer(Quad) · 同材质 · 每物体不同 MaterialPropertyBlock · Instancing ON",
        "MeshRenderer(Quad) · 同材质 · 不打 MaterialPropertyBlock · Instancing ON（看实例上限）",
        "MeshRenderer(Quad) · MaterialPropertyBlock 里放『非实例化属性』 · Instancing ON（对照）",
    };

    private static readonly string[] ScenarioNotes =
    {
        "材质 Instancing OFF（默认）+ 不打 MaterialPropertyBlock：SpriteRenderer 同材质同图集 → 期望合成很少几次 draw（合批基线）。",
        "材质 Instancing OFF（默认）+ 每物体不同 MaterialPropertyBlock。★ 核心问题：同一个材质、MaterialPropertyBlock 里的 _Color/_Value 各不相同 —— 必须两个判据一起看：净 Batches≈物体数 且外观不同 → 值不同必然切批（一次 draw 只能一份 uniform）；净 Batches≈1 且外观也相同 → 逐物体值没生效；净 Batches≈1 且外观不同 → 才是真的『不同值也能合批』。",
        "材质勾了 Enable GPU Instancing，但不打 MaterialPropertyBlock：看 SpriteRenderer 会不会走材质实例化（BiRP 下 SpriteRenderer 通常不参与材质实例化，预期与场景 1 接近）。",
        "★ 材质 Instancing ON + 每物体不同 MaterialPropertyBlock：若 SpriteRenderer 支持实例化，这里应比场景 2 更省或持平；若与场景 2 完全一样，说明这个开关对 SpriteRenderer 无效。",
        "对照：不是 MaterialPropertyBlock，而是给每个物体 new 一个 Material（或访问 renderer.material，每个副本的实例化都是关的）→ 材质副本爆炸，每个物体必然一次 draw。",
        "期望：MeshRenderer 同材质同网格 → 很少几次 draw（静态/动态合批基线）。",
        "同材质 + MaterialPropertyBlock 不同 + 关实例化：MaterialPropertyBlock 让每个物体的材质状态不同 → 通常会每个物体一次 draw（无实例化可用）。",
        "同材质 + MaterialPropertyBlock 不同 + 开实例化：Unity 把 MaterialPropertyBlock 里的每实例值塞进实例缓冲 → 一次 draw 画一批（受实例数上限影响）。",
        "不塞 MaterialPropertyBlock，只靠变换做实例化：能直观看到每个批次能装多少实例（Unity 按常量缓冲上限推算，默认上限 500~1023 视平台）。",
        "把非实例化属性 _NonInstanced 放进 MaterialPropertyBlock：官方文档说会关闭实例化 —— 期望净 Batches 从『一批一次』变成『一个物体一次』。",
    };

    // ================= 运行时状态 =================

    private readonly List<GameObject> _objects = new List<GameObject>();

    /// <summary>
    /// 只复用一个块：值在 SetPropertyBlock 时被拷贝，可以安全复用。
    /// 注意：<b>不能在字段初始化器里 new Unity 对象</b>（Unity 会抛
    /// "CreateImpl is not allowed to be called from a MonoBehaviour constructor"），必须在 Awake 里创建。
    /// </summary>
    private MaterialPropertyBlock _block;

    private Material _shared;      // 共享材质（Enable GPU Instancing = false）
    private Material _instanced;   // 共享材质（Enable GPU Instancing = true）
    private Texture2D _texture;
    private Sprite _sprite;
    private Mesh _quad;
    private Camera _camera;

    private int _scenario;
    private bool _hidden;
    private bool _dirty;

    // 统计（30 帧平均）：取 Profiler 的 Render 计数器（公开 API，Editor / 真机 都能读；
    // 计数器名在个别版本可能不存在，所以用 ProfilerRecorder.Valid 兜住，读不到就显示 n/a）
    private ProfilerRecorder _statBatches, _statDrawCalls, _statSetPass;
    private double _sumBatches, _sumDrawCalls, _sumSetPass;
    private int _frames;
    private double _batches, _drawCalls, _setPass;
    private bool _logged;

    // 空场景基线（按空格时自动记录）：编辑器/Scene 视图/UI 等固定底噪都在里面，净增量要减掉它
    private bool _baselineCaptured;
    private double _baselineBatches, _baselineDrawCalls, _baselineSetPass;

    // 外观自检：把场景渲到小 RT 再读回若干物体中心的颜色，判断"逐物体外观是否真的不同"
    private RenderTexture _probe;
    private Texture2D _probeRead;
    private int _sampleCount;
    private int _sampleDistinct = -1;
    private bool _sampleAllBackground;   // 采样点全是背景色 → 探针本身不可靠，不能当作"逐物体值没生效"

    private GUIStyle _titleStyle, _bodyStyle;

    // ================= 生命周期 =================

    private void Awake()
    {
        if (testShader == null) testShader = Shader.Find("Customer/MaterialPropertyBlock/MaterialPropertyBlockTest");
        if (testShader == null)
        {
            Debug.LogError("[MaterialPropertyBlock 测试] 找不到着色器 Customer/MaterialPropertyBlock/MaterialPropertyBlockTest —— 请把 MaterialPropertyBlockTest.shader 放到工程里，" +
                           "或把它拖到本组件的 testShader 字段上。");
            enabled = false;
            return;
        }

        _texture = CreateSpriteTexture(64);
        _sprite = Sprite.Create(_texture, new Rect(0, 0, _texture.width, _texture.height),
                                new Vector2(0.5f, 0.5f), _texture.width);
        _sprite.name = "MaterialPropertyBlockTestSprite";
        _quad = CreateQuadMesh();

        _shared = new Material(testShader) { name = "MaterialPropertyBlockTest-Shared", enableInstancing = false };
        _shared.SetTexture("_MainTex", _texture);
        _instanced = new Material(testShader) { name = "MaterialPropertyBlockTest-Instanced", enableInstancing = true };
        _instanced.SetTexture("_MainTex", _texture);

        // Unity 对象必须在 Awake/Start 里创建（不能放字段初始化器）
        _block = new MaterialPropertyBlock();

        _statBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _statDrawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _statSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");

        EnsureCamera();
        _dirty = true;
    }

    private void OnEnable() => _dirty = true;

    private void Update()
    {
        HandleKeys();

        if (_dirty)
        {
            _dirty = false;
            Rebuild();
        }

        AccumulateStats();
    }

    private void OnDestroy()
    {
        DestroyAll();
        if (_shared != null) Destroy(_shared);
        if (_instanced != null) Destroy(_instanced);
        if (_quad != null) Destroy(_quad);
        if (_sprite != null) Destroy(_sprite);
        if (_texture != null) Destroy(_texture);
        if (_probe != null) Destroy(_probe);
        if (_probeRead != null) Destroy(_probeRead);

        if (_statBatches.Valid) _statBatches.Dispose();
        if (_statDrawCalls.Valid) _statDrawCalls.Dispose();
        if (_statSetPass.Valid) _statSetPass.Dispose();
    }

    // ================= 场景构建 =================

    private void Rebuild()
    {
        DestroyAll();
        ResetStats();

        const int count = ObjectCount;

        switch (_scenario)
        {
            // SpriteRenderer 组：材质 Instancing 开关 × 是否打 MaterialPropertyBlock
            case 0: CreateSprites(count, withBlock: false, ownMaterial: false, instancing: false); break;
            case 1: CreateSprites(count, withBlock: true, ownMaterial: false, instancing: false); break;
            case 2: CreateSprites(count, withBlock: false, ownMaterial: false, instancing: true); break;
            case 3: CreateSprites(count, withBlock: true, ownMaterial: false, instancing: true); break;
            case 4: CreateSprites(count, withBlock: false, ownMaterial: true, instancing: false); break;

            // MeshRenderer(Quad) 组：实例化的标准用法
            case 5: CreateQuads(count, withBlock: false, instancing: false, nonInstancedInBlock: false); break;
            case 6: CreateQuads(count, withBlock: true, instancing: false, nonInstancedInBlock: false); break;
            case 7: CreateQuads(count, withBlock: true, instancing: true, nonInstancedInBlock: false); break;
            case 8: CreateQuads(count, withBlock: false, instancing: true, nonInstancedInBlock: false); break;
            default: CreateQuads(count, withBlock: true, instancing: true, nonInstancedInBlock: true); break;
        }

        FrameCamera(count);
    }

    private void CreateSprites(int count, bool withBlock, bool ownMaterial, bool instancing)
    {
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("Sprite_" + i);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = GridPosition(i, count);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _sprite;
            sr.color = Color.white;
            sr.shadowCastingMode = ShadowCastingMode.Off;
            sr.receiveShadows = false;

            // 材质三选一：ownMaterial = 每物体一个副本（对照，必然打断合批）；
            // 否则按 instancing 选共享材质（Enable GPU Instancing 开 / 关）。
            Material material = ownMaterial
                ? new Material(testShader) { name = "MaterialPropertyBlockTest-Own_" + i, enableInstancing = false }
                : (instancing ? _instanced : _shared);
            if (ownMaterial) material.SetTexture("_MainTex", _texture);
            sr.sharedMaterial = material;

            if (withBlock)
            {
                _block.Clear();
                _block.SetColor("_Color", TintFor(i));
                _block.SetFloat("_Value", ValueFor(i));
                sr.SetPropertyBlock(_block);
            }

            _objects.Add(go);
        }
    }

    private void CreateQuads(int count, bool withBlock, bool instancing, bool nonInstancedInBlock)
    {
        Material material = instancing ? _instanced : _shared;

        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("Quad_" + i);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = GridPosition(i, count);
            go.transform.localScale = Vector3.one * 0.9f;

            go.AddComponent<MeshFilter>().sharedMesh = _quad;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            if (withBlock)
            {
                _block.Clear();
                _block.SetColor("_Color", TintFor(i));
                _block.SetFloat("_Value", ValueFor(i));

                // 场景 8：故意塞一个"非实例化属性"，验证官方那句"会关掉实例化"
                if (nonInstancedInBlock) _block.SetFloat("_NonInstanced", 0.25f);

                mr.SetPropertyBlock(_block);
            }

            _objects.Add(go);
        }
    }

    private void DestroyAll()
    {
        for (int i = 0; i < _objects.Count; i++)
        {
            if (_objects[i] != null)
            {
                // 对照场景里每个物体有自己的材质，这里一并释放
                var sr = _objects[i].GetComponent<SpriteRenderer>();
                if (sr != null && sr.sharedMaterial != null && sr.sharedMaterial != _shared && sr.sharedMaterial != _instanced)
                    Destroy(sr.sharedMaterial);

                Destroy(_objects[i]);
            }
        }
        _objects.Clear();
    }

    private void SetObjectsActive(bool active)
    {
        for (int i = 0; i < _objects.Count; i++)
            if (_objects[i] != null) _objects[i].SetActive(active);
    }

    private static Color TintFor(int index) => Color.HSVToRGB(Mathf.Repeat(index * 0.137f, 1f), 0.72f, 1f);

    private static float ValueFor(int index) => 0.55f + 0.45f * Mathf.Repeat(index * 0.021f, 1f);

    private static Vector3 GridPosition(int index, int count)
    {
        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(count)));
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)cols));
        int c = index % cols;
        int r = index / cols;
        return new Vector3((c - (cols - 1) * 0.5f), ((rows - 1) * 0.5f - r), 0f);
    }

    private void FrameCamera(int count)
    {
        if (_camera == null) return;

        int cols = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(count)));
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)cols));
        float halfH = rows * 0.5f * 1.25f + 1f;
        float halfW = cols * 0.5f * 1.25f + 1f;
        float aspect = Mathf.Max(0.2f, (float)Screen.width / Mathf.Max(1, Screen.height));

        _camera.orthographic = true;
        _camera.orthographicSize = Mathf.Max(halfH, halfW / aspect);
        _camera.transform.position = new Vector3(0f, 0f, -10f);
        _camera.transform.rotation = Quaternion.identity;
    }

    // ================= 输入 =================

    private void HandleKeys()
    {
        // 1~9 → 场景 1~9；0 → 场景 10（共 10 个对照）；空格 → 空场景基线（隐藏/显示全部物体）；R → 重新随机颜色
        for (int i = 0; i < ScenarioTitles.Length - 1; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
            {
                SetScenario(i);
                return;
            }
        }

        if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))
        {
            SetScenario(ScenarioTitles.Length - 1);
            return;
        }

        if (Input.GetKeyDown(KeyCode.Space))
        {
            _hidden = !_hidden;
            SetObjectsActive(!_hidden);
            ResetStats();
        }

        if (Input.GetKeyDown(KeyCode.R)) Rebuild();
    }

    private void SetScenario(int index)
    {
        _scenario = Mathf.Clamp(index, 0, ScenarioTitles.Length - 1);
        _hidden = false;
        Rebuild();
    }

    // ================= 统计 =================

    private void ResetStats()
    {
        _sumBatches = _sumDrawCalls = _sumSetPass = 0;
        _frames = 0;
        _batches = _drawCalls = _setPass = 0;
        _logged = false;
    }

    private void AccumulateStats()
    {
        _sumBatches += ReadStat(_statBatches);
        _sumDrawCalls += ReadStat(_statDrawCalls);
        _sumSetPass += ReadStat(_statSetPass);
        _frames++;

        if (_frames < StatWindow) return;

        _batches = _sumBatches / _frames;
        _drawCalls = _sumDrawCalls / _frames;
        _setPass = _sumSetPass / _frames;
        _sumBatches = _sumDrawCalls = _sumSetPass = 0;
        _frames = 0;

        // 空场景的这一段读数就是"固定底噪"，记下来给其它场景做减法
        if (_hidden)
        {
            _baselineCaptured = true;
            _baselineBatches = _batches;
            _baselineDrawCalls = _drawCalls;
            _baselineSetPass = _setPass;
        }

        // 外观自检：放在平均值算完之后（它自己会额外渲一帧）；空场景没有物体，跳过
        if (_hidden || _objects.Count == 0)
        {
            _sampleCount = 0;
            _sampleDistinct = -1;
        }
        else
        {
            SampleAppearance();
        }

        if (_logged) return;
        _logged = true;

        string net = _baselineCaptured && !_hidden
            ? $"｜净增量(减基线) Batches≈{_batches - _baselineBatches:F0} DrawCalls≈{_drawCalls - _baselineDrawCalls:F0}"
            : string.Empty;

        string look;
        if (_sampleDistinct < 0)
            look = string.Empty;
        else if (_sampleAllBackground)
            look = $"｜外观自检 采样{_sampleCount}点全是背景色(探针不可靠，本项忽略)";
        else
            look = $"｜外观自检 采样{_sampleCount}点→{_sampleDistinct}种颜色" +
                   (_sampleDistinct >= 2 ? "(逐物体不同 ✓)" : "(全都一样 ⚠ 逐物体值可能没生效)");

        Debug.Log($"[MaterialPropertyBlock 测试] 场景 {_scenario + 1}｜{ScenarioTitles[_scenario]}｜物体 {(_hidden ? 0 : _objects.Count)}｜" +
                  $"Batches≈{FormatStat(_batches, _statBatches)}  DrawCalls≈{FormatStat(_drawCalls, _statDrawCalls)}  " +
                  $"SetPassCalls≈{FormatStat(_setPass, _statSetPass)}{net}{look}");
    }

    /// <summary>读一个渲染计数器；该计数器在当前版本/平台不存在时返回 0（界面显示 n/a）。</summary>
    private static long ReadStat(ProfilerRecorder recorder) => recorder.Valid ? recorder.LastValue : 0L;

    private static string FormatStat(double value, ProfilerRecorder recorder)
        => recorder.Valid ? value.ToString("F0") : "n/a";

    /// <summary>
    /// 外观自检：把当前场景渲染进一张小 RenderTexture，读回若干物体中心的像素颜色，看它们是否真的逐物体不同。
    /// <para>
    /// 为什么必须做这一步：只看 DrawCall 会误判 —— 如果测出"净 Batches ≈ 1"，有两种完全相反的解释：
    /// <list type="bullet">
    ///   <item>逐物体值生效了，而且 Unity 把不同值的对象也合并了（颜色应当各不相同）；</item>
    ///   <item>逐物体值根本没生效（所有物体渲染成同一个样子，被当成完全相同的东西合并了）。</item>
    /// </list>
    /// 颜色采样能把这两种情况区分开。
    /// </para>
    /// <para>注意：它自己会额外渲染一帧，所以只在"统计平均已经算完"之后调用。</para>
    /// </summary>
    private void SampleAppearance()
    {
        if (_camera == null || _objects.Count == 0) return;

        // 探针 RT 的宽高比必须和屏幕一致：否则相机会改用 RT 的宽高比取景，采样点可能落到画面外
        const int probeWidth = 256;
        int probeHeight = Mathf.Clamp(
            Mathf.RoundToInt(probeWidth * (float)Screen.height / Mathf.Max(1, Screen.width)), 32, 512);

        if (_probe == null || _probe.width != probeWidth || _probe.height != probeHeight)
        {
            if (_probe != null) Destroy(_probe);
            if (_probeRead != null) Destroy(_probeRead);

            _probe = new RenderTexture(probeWidth, probeHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "MaterialPropertyBlockTestProbe",
            };
            _probeRead = new Texture2D(probeWidth, probeHeight, TextureFormat.RGBA32, false)
            {
                name = "MaterialPropertyBlockTestProbeRead",
            };
        }

        RenderTexture previousTarget = _camera.targetTexture;
        _camera.targetTexture = _probe;

        // 采样点必须在 targetTexture 还是 RT 时算出来 —— WorldToScreenPoint 返回的是"当前 target"的像素坐标，
        // 若等恢复成屏幕之后再算，坐标就与 RT 对不上了（这正是上一版"全都一样"的原因）。
        int step = Mathf.Max(1, _objects.Count / 24);
        var points = new List<Vector2Int>(24);
        for (int i = 0; i < _objects.Count; i += step)
        {
            if (_objects[i] == null) continue;

            Vector3 screen = _camera.WorldToScreenPoint(_objects[i].transform.position);
            points.Add(new Vector2Int(
                Mathf.Clamp((int)screen.x, 0, _probe.width - 1),
                Mathf.Clamp((int)screen.y, 0, _probe.height - 1)));
        }

        _camera.Render();
        _camera.targetTexture = previousTarget;

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = _probe;
        _probeRead.ReadPixels(new Rect(0f, 0f, _probe.width, _probe.height), 0, 0);
        _probeRead.Apply();
        RenderTexture.active = previousActive;

        Color32 background = Quantize(_probeRead.GetPixel(0, 0));
        var distinct = new List<Color32>(24);
        bool allBackground = true;

        for (int i = 0; i < points.Count; i++)
        {
            Color32 q = Quantize(_probeRead.GetPixel(points[i].x, points[i].y));
            if (!distinct.Contains(q)) distinct.Add(q);
            if (!q.Equals(background)) allBackground = false;
        }

        _sampleCount = points.Count;
        _sampleDistinct = distinct.Count;
        _sampleAllBackground = allBackground;
    }

    /// <summary>量化到 16 级，避免抗锯齿/边缘的微小差异被当成"外观不同"。</summary>
    private static Color32 Quantize(Color32 c)
        => new((byte)(c.r >> 4), (byte)(c.g >> 4), (byte)(c.b >> 4), 255);

    // ================= 屏幕信息 =================

    private void OnGUI()
    {
        if (_titleStyle == null)
        {
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, wordWrap = true };
            _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
        }

        const float width = 760f;
        GUILayout.BeginArea(new Rect(12f, 12f, width, 320f), GUI.skin.box);

        GUI.color = new Color(0.6f, 0.85f, 1f);
        GUILayout.Label($"场景 {_scenario + 1}/{ScenarioTitles.Length}：{ScenarioTitles[_scenario]}", _titleStyle);
        GUI.color = Color.white;

        int shown = _hidden ? 0 : _objects.Count;
        GUILayout.Label($"物体数：{shown}（每场景固定 {ObjectCount}）   物体已隐藏：{_hidden}（按空格切换）", _bodyStyle);

        GUI.color = new Color(1f, 0.85f, 0.45f);
        GUILayout.Label($"Batches≈{FormatStat(_batches, _statBatches)}    " +
                        $"DrawCalls≈{FormatStat(_drawCalls, _statDrawCalls)}    " +
                        $"SetPassCalls≈{FormatStat(_setPass, _statSetPass)}" +
                        (_frames > 0 ? "（统计中…）" : "（30 帧平均）"), _titleStyle);
        GUI.color = Color.white;

        GUI.color = new Color(0.7f, 0.95f, 0.7f);
        if (!_baselineCaptured)
            GUILayout.Label("还没测空场景基线：按空格隐藏物体会自动记录 —— 这个读数面板自己也要占几次 draw，" +
                            "所以必须先记基线、看净增量。", _bodyStyle);
        else if (!_hidden)
            GUILayout.Label($"净增量（减空场景基线）≈ Batches {_batches - _baselineBatches:F0}    " +
                            $"DrawCalls {_drawCalls - _baselineDrawCalls:F0}", _titleStyle);
        else
            GUILayout.Label($"空场景基线已记录：Batches {_baselineBatches:F0} / DrawCalls {_baselineDrawCalls:F0}（再按空格恢复显示）", _bodyStyle);

        if (_sampleDistinct >= 0)
        {
            GUI.color = _sampleDistinct >= 2 && !_sampleAllBackground
                ? new Color(0.7f, 0.95f, 0.7f)
                : new Color(1f, 0.62f, 0.5f);

            string verdict = _sampleAllBackground
                ? "采样点全是背景色（探针渲染/坐标不可靠，本项忽略）"
                : (_sampleDistinct >= 2
                    ? "逐物体外观确实不同 ✓（那么 DC 小才是真合批）"
                    : "所有采样点颜色相同 ⚠（逐物体值很可能没生效，别把 DC 小当成合批）");

            GUILayout.Label($"外观自检：采样 {_sampleCount} 点 → {_sampleDistinct} 种颜色 —— {verdict}", _bodyStyle);
        }
        GUI.color = Color.white;

        GUILayout.Space(4f);
        GUILayout.Label(ScenarioNotes[_scenario], _bodyStyle);

        GUILayout.Space(4f);
        GUI.color = new Color(0.7f, 0.9f, 0.7f);
        GUILayout.Label("判读：数值 ≈ 物体数 = 每个物体一次 draw（没合批）；≈1~2 = 全部合并；中间值通常是被『每批实例上限』分块。", _bodyStyle);
        GUILayout.Label("原因看 Window > Analysis > Frame Debugger：选中某次 draw，它会写明为什么没和上一次合并。", _bodyStyle);
        GUILayout.Label("注意：统计含场景里所有可见相机的绘制 —— 建议最大化 Game 视口（或关掉 Scene 视图的实时渲染），读数才干净。", _bodyStyle);
        GUI.color = Color.white;

        GUILayout.Label("按键：1~9 切场景 1~9 · 0 切场景 10 · 空格 空场景基线（隐藏/显示物体） · R 重新随机颜色", _bodyStyle);

        GUILayout.EndArea();
    }

    // ================= 资源 =================

    private void EnsureCamera()
    {
        _camera = Camera.main;
        if (_camera != null) return;

        var go = new GameObject("MaterialPropertyBlock Test Camera") { tag = "MainCamera" };
        _camera = go.AddComponent<Camera>();
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = new Color(0.06f, 0.07f, 0.10f, 1f);
    }

    private static Texture2D CreateSpriteTexture(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "MaterialPropertyBlockTestSpriteTexture",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };

        var pixels = new Color32[size * size];
        float center = (size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center, dy = y - center;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01((radius - dist) / 2f);
                // 边缘再压一圈暗色，方便看清每个物体的边界
                byte edge = (byte)(Mathf.Clamp01(1f - dist / radius) * 90f);
                pixels[y * size + x] = new Color32((byte)(120 + edge), (byte)(150 + edge), (byte)(200 + edge), (byte)(alpha * 255f));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }

    private static Mesh CreateQuadMesh()
    {
        var mesh = new Mesh { name = "MaterialPropertyBlockTestQuad" };
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
        };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
        };
        mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    // ================= 兜底：场景里没有本组件时也能跑 =================

    /// <summary>
    /// 万一场景里的脚本引用失效（比如 GUID 被工具改了），只要场景名是 MaterialPropertyBlockTest，
    /// 播放时也会自动挂上本组件 —— 保证这个测试页永远能跑起来。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoBoot()
    {
        if (SceneManager.GetActiveScene().name != "MaterialPropertyBlockTest") return;
        if (FindObjectOfType<MaterialPropertyBlockTest>() != null) return;
        new GameObject("MaterialPropertyBlock Test").AddComponent<MaterialPropertyBlockTest>();
    }
}
