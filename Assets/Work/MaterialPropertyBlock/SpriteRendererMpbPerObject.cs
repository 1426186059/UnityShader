using UnityEngine;
[DisallowMultipleComponent]
[RequireComponent(typeof(SpriteRenderer))]
public sealed class SpriteRendererMpbPerObject : MonoBehaviour
{
    public Color Tint = Color.white;
    private MaterialPropertyBlock _block;
    private SpriteRenderer _renderer;
    
    private void Awake()
    {
        _renderer = GetComponent<SpriteRenderer>();
        _block = new MaterialPropertyBlock();
    }
    
    private void LateUpdate()
    {
        _block.SetColor("_Color", Tint);
        _renderer.SetPropertyBlock(_block);
    }
}
