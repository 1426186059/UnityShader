using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 新场景 <c>SpriteMpbPerObject.unity</c> 的搭建与统计。
///
/// <para>
/// 场景本身只有一台相机（它会在 Play 时自动挂上本组件，见 <see cref="AutoBoot"/>），
/// 12 个物体由本脚本在运行时生成；每个物体的 GameObject 上挂一个
/// <see cref="SpriteRendererMpbPerObject"/> —— 也就是<b>每个物体各自持有一份 MaterialPropertyBlock</b>。
/// </para>
///
/// <para>
/// 对照：上一个场景（<c>SpriteRenderer.unity</c>）是<b>所有物体共用同一份</b> MaterialPropertyBlock。
/// 本场景测的就是"共用 vs 各自一份"是不是影响合批的那个变量。
/// </para>
///
/// <para><b>按键</b>：<c>1</c> 各自 MPB 设 <c>_Color</c>（默认）· <c>2</c> 各自 MPB 设 <c>_RendererColor</c> ·
/// <c>3</c> 各自 <c>sr.color</c>（不走 MPB）· <c>4</c> 切换材质 Enable GPU Instancing。</para>
///
/// <para>每秒往 Console 打一行 <c>[SR-MPB-PEROBJ]</c> 读数（Batches / DrawCalls / SetPassCalls）。</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class SpriteMpbPerObjectScene : MonoBehaviour
{
    private const int Count = 12;

    private static readonly string[] ModeNames =
    {
        "各自 MPB 设 _Color",
        "各自 MPB 设 _RendererColor",
        "各自 sr.color（不走 MPB）",
    };

    private readonly List<GameObject> _objects = new List<GameObject>();

    private Texture2D _texture;
    private Sprite _sprite;
    private Material _material;
    private bool _enableInstancing;
    private int _mode;
    private float _nextLog;

    private ProfilerRecorder _statBatches, _statDrawCalls, _statSetPass;
    private GUIStyle _titleStyle, _bodyStyle;

    private void Awake()
    {
        _texture = CreateWhiteTexture();
        _sprite = Sprite.Create(_texture, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), 2f);  // 2px / PPU 2 = 1 世界单位
        _material = new Material(Shader.Find("Sprites/Default"))
        {
            name = "SpritesDefault-Shared",
            enableInstancing = _enableInstancing,
        };

        _statBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _statDrawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _statSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");

        Rebuild();
    }

    private void OnDestroy()
    {
        for (int i = 0; i < _objects.Count; i++)
            if (_objects[i] != null) Destroy(_objects[i]);
        _objects.Clear();

        if (_material != null) Destroy(_material);
        if (_sprite != null) Destroy(_sprite);
        if (_texture != null) Destroy(_texture);

        if (_statBatches.Valid) _statBatches.Dispose();
        if (_statDrawCalls.Valid) _statDrawCalls.Dispose();
        if (_statSetPass.Valid) _statSetPass.Dispose();
    }

    // ================= 场景搭建 =================

    private void Rebuild()
    {
        for (int i = 0; i < _objects.Count; i++)
            if (_objects[i] != null) Destroy(_objects[i]);
        _objects.Clear();

        for (int i = 0; i < Count; i++)
        {
            var go = new GameObject("Sprite_" + i);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(i % 4 * 1.3f - 1.95f, i / 4 * 1.3f - 1.3f, 0f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _sprite;
            sr.sharedMaterial = _material;     // ★ 所有物体共用同一个材质

            // ★ 每个物体一个组件实例，组件内部各自 new 一份 MaterialPropertyBlock
            var tinter = go.AddComponent<SpriteRendererMpbPerObject>();
            tinter.Tint = TintFor(i);

            _objects.Add(go);
        }

        Debug.Log($"[SR-MPB-PEROBJ] 重建｜{ModeNames[_mode]}｜材质 Instancing={_enableInstancing}｜物体={Count}（每个物体各持一份 MPB）");
    }

    private static Color TintFor(int index) => Color.HSVToRGB(index / (float)Count, 0.75f, 1f);

    private static Texture2D CreateWhiteTexture()
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "SpriteMpbPerObjectWhite" };
        texture.SetPixels32(new[] { (Color32)Color.white, (Color32)Color.white, (Color32)Color.white, (Color32)Color.white });
        texture.Apply();
        return texture;
    }

    // ================= 输入与统计 =================

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1)) { _mode = 0; Rebuild(); }
        if (Input.GetKeyDown(KeyCode.Alpha2)) { _mode = 1; Rebuild(); }
        if (Input.GetKeyDown(KeyCode.Alpha3)) { _mode = 2; Rebuild(); }

        if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            _enableInstancing = !_enableInstancing;
            if (_material != null) _material.enableInstancing = _enableInstancing;
            Debug.Log($"[SR-MPB-PEROBJ] 材质 Enable GPU Instancing = {_enableInstancing}");
        }

        if (Time.unscaledTime < _nextLog) return;
        _nextLog = Time.unscaledTime + 1f;

        Debug.Log($"[SR-MPB-PEROBJ] {ModeNames[_mode]}｜Instancing={_enableInstancing}｜物体={Count}｜" +
                  $"Batches={Stat(_statBatches)} DrawCalls={Stat(_statDrawCalls)} SetPassCalls={Stat(_statSetPass)}");
    }

    private static string Stat(ProfilerRecorder recorder) => recorder.Valid ? recorder.LastValue.ToString() : "n/a";

    private void OnGUI()
    {
        if (_titleStyle == null)
        {
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = true };
            _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
        }

        GUILayout.BeginArea(new Rect(12f, 12f, 700f, 200f), GUI.skin.box);

        GUI.color = new Color(0.6f, 0.85f, 1f);
        GUILayout.Label($"每个物体各持一份 MPB｜{ModeNames[_mode]}｜Instancing={_enableInstancing}", _titleStyle);
        GUI.color = Color.white;

        GUILayout.Label($"物体数 {Count}（材质 Sprites/Default 共用一份；MPB 每个物体一份，不共用也不循环复用）", _bodyStyle);

        GUI.color = new Color(1f, 0.85f, 0.45f);
        GUILayout.Label($"Batches={Stat(_statBatches)}    DrawCalls={Stat(_statDrawCalls)}    SetPassCalls={Stat(_statSetPass)}", _titleStyle);
        GUI.color = Color.white;

        GUILayout.Label("读法：≈12 = 每个物体一次 draw（没合批）；≈1~2 = 合并成立。对着上一场景（共用一份 MPB）比数值即可。", _bodyStyle);
        GUILayout.Label("按键：1 各自MPB设_Color · 2 各自MPB设_RendererColor · 3 各自sr.color · 4 切换 Instancing", _bodyStyle);

        GUILayout.EndArea();
    }

    /// <summary>场景名是 SpriteMpbPerObject 时自动挂载，无需手动加组件。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoBoot()
    {
        if (SceneManager.GetActiveScene().name != "SpriteMpbPerObject") return;
        if (FindObjectOfType<SpriteMpbPerObjectScene>() != null) return;
        new GameObject("SpriteMpbPerObject Scene").AddComponent<SpriteMpbPerObjectScene>();
    }
}
