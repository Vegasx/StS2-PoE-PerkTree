shader_type canvas_item;
render_mode unshaded;

// Keep CanvasItem modulate/self_modulate RGB white; use branch_color for hue.
uniform vec4 branch_color : source_color = vec4(0.42, 0.69, 1.0, 1.0);
uniform int tint_mode : hint_enum("Border only", "Whole coin") = 0;
uniform float tint_strength : hint_range(0.0, 1.0) = 1.0;

// Calibrated to the existing, square, padded perk-tokens-v1 PNG exports.
uniform vec2 coin_center = vec2(0.5, 0.5);
uniform float rim_start : hint_range(0.0, 0.5) = 0.335;
uniform float rim_end : hint_range(0.0, 0.5) = 0.347;

// Default: an individual PNG. For an atlas, supply the normalized UV rectangle
// of this coin's full 256x256 region, including the original padding.
uniform vec4 coin_uv_rect = vec4(0.0, 0.0, 1.0, 1.0);

varying vec4 item_color;

void vertex() {
    item_color = COLOR;
}

void fragment() {
    vec4 original = texture(TEXTURE, UV);
    vec2 local_uv = (UV - coin_uv_rect.xy) / coin_uv_rect.zw;
    float radius = distance(local_uv, coin_center);
    float border = smoothstep(rim_start, rim_end, radius);

    // Remove the existing hue before tinting. Max-channel brightness keeps
    // the rim's painted shading without the muddy yellow-times-blue result.
    float shade = max(original.r, max(original.g, original.b));
    vec3 themed = shade * branch_color.rgb;
    float mask = tint_mode == 0 ? border : 1.0;
    vec3 rgb = mix(original.rgb, themed, mask * tint_strength);

    // Retain original transparency and normal CanvasItem modulation/fades.
    COLOR = vec4(rgb, original.a * branch_color.a) * item_color;
}
