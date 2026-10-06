using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 最小例子：一个共享的 SpriteRenderer 默认材质（Sprites/Default）+ 每个物体自己的逐物体颜色，
/// 看能不能合批（颜色走哪条通道由 tintSource 决定）。
///
/// <para>
/// 怎么读结果（不用写统计代码）：
///   Window &gt; Analysis &gt; Stats（或 Game 视图右上角 Stats）看 Batches / DrawCalls；
///   Window &gt; Analysis &gt; Frame Debugger 选中某次 draw，它会直接写明为什么没和上一次合并。
/// </para>
///
/// <para>
/// 翻 Inspector 上这两个开关做对照，变量才能单独摘出来：
///   enableInstancing —— 材质上的 Enable GPU Instancing；
///   tintSource       —— 逐物体颜色走哪条通道（见下面的枚举）。
/// </para>
///
/// <para>
/// 依据（内置着色器源码 CGIncludes/UnitySprites.cginc）：
///   UNITY_INSTANCING_ENABLED 时 _RendererColor 被声明成 UNITY_DEFINE_INSTANCED_PROP（逐实例数组，
///   由 SpriteRenderer 自己填写），而 _Color 永远是一份普通 uniform —— 一次 draw 只有一份值。
/// </para>
///
/// <para>
/// 时序说明：物体在 <see cref="Start"/> 建好，<b>逐物体数据每帧在 <see cref="LateUpdate"/> 重新下发</b>
/// （渲染前最后一次机会），用来排除"只在 Start 设一次、之后被 Unity 覆盖"这类时序怀疑。
/// </para>
///
/// <para>
/// 实测结论（Unity 2022.3 · 内置管线 · Sprites/Default，20 个物体）：
///   MPB 写 _Color 各不相同         → 不合批，Batches ≈ 20（_Color 是普通 uniform）；
///   MPB 写 _RendererColor 各不相同 → 值被忽略，全部按 SpriteRenderer.color 渲染（写了等于没写）；
///   SpriteRenderer.color 各不相同  → 合批（颜色由顶点色 / 实例数组承载）。
/// 即：SpriteRenderer 上承载"逐物体不同数据"的通道是 SpriteRenderer.color，不是 MaterialPropertyBlock。
/// </para>
/// </summary>
[DisallowMultipleComponent]
public sealed class SpriteRendererMpbSimple : MonoBehaviour
{
    /// <summary>逐物体颜色走哪条通道（三选一，用来把变量单独摘出来）。</summary>
    private enum TintSource
    {
        /// <summary>SpriteRenderer.color：顶点色 / 实例数组，Unity 自己填（唯一能合批的一条）。</summary>
        SpriteRendererColor,
        /// <summary>MaterialPropertyBlock 的 _Color：普通 uniform，一次 draw 一份值。</summary>
        MpbColor,
        /// <summary>MaterialPropertyBlock 的 _RendererColor：已被 Unity 内部接管，写了不生效。</summary>
        MpbRendererColor,
    }

    [SerializeField] private int objectCount = 20;
    [SerializeField] private bool enableInstancing;
    [SerializeField] private TintSource tintSource;

    private readonly List<SpriteRenderer> _sprites = new List<SpriteRenderer>();
    private readonly List<Color> _tints = new List<Color>();
    private MaterialPropertyBlock _block;

    private void Start()
    {
        // SpriteRenderer 的默认材质：Sprites/Default（不要给它设 _MainTex，
        // 该属性有 [PerRendererData]，贴图由 SpriteRenderer 自己按 sprite 提供）
        var material = new Material(Shader.Find("Sprites/Default"))
        {
            name = "SpritesDefault-Shared",
        };

        Debug.Log("material.enableInstancing 默认: " + material.enableInstancing);
        material.enableInstancing = enableInstancing;

        // 白色方块精灵：逐物体的颜色差异按 tintSource 走不同通道（见上面的枚举与实测结论）
        Sprite sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 4f);

        // 只复用一个块：值在 SetPropertyBlock 时被拷贝到该渲染器上，所以复用是安全的
        _block = new MaterialPropertyBlock();

        for (int i = 0; i < objectCount; i++)
        {
            var go = new GameObject("Sprite_" + i);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(i % 5 * 1.1f - 2.2f, i / 5 * 1.1f - 2.2f, 0f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sharedMaterial = material;   // ★ 所有物体共用同一个材质
            sr.color = Color.white;

            _sprites.Add(sr);
            _tints.Add(Color.HSVToRGB(i / (float)objectCount, 0.8f, 1f));   // ★ 每个物体一个颜色
        }
    }

    /// <summary>
    /// 逐物体数据每帧重新下发（渲染前最后一次机会）。只写当前 tintSource 需要的那条通道，
    /// 不去碰其它通道，避免"顺手多写一笔"本身改变合批结果。
    /// </summary>
    private void LateUpdate()
    {
        for (int i = 0; i < _sprites.Count; i++)
        {
            SpriteRenderer sr = _sprites[i];
            if (sr == null) continue;

            Color tint = _tints[i];

            switch (tintSource)
            {
                case TintSource.SpriteRendererColor:
                    sr.color = tint;                    // 逐物体颜色：Unity 通过顶点色 / 实例数组承载
                    break;

                case TintSource.MpbColor:
                    _block.Clear();
                    _block.SetColor("_Color", tint);     // 普通 uniform：一次 draw 只能一份值
                    sr.SetPropertyBlock(_block);
                    break;

                case TintSource.MpbRendererColor:
                    _block.Clear();
                    _block.SetColor("_RendererColor", tint);   // 写了没用：Unity 随后用自己的值覆盖
                    sr.SetPropertyBlock(_block);
                    break;
            }
        }
    }
}
