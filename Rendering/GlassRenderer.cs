using System;

namespace ClassIsland.LiquidGlass.Rendering;

/// <summary>玻璃外壳的渲染参数。</summary>
internal readonly record struct GlassParams(
    float CornerRadius,
    float Thickness,
    float LightRadians,
    float LightIntensity,
    float Ambient,
    float Dispersion,
    float TintR,
    float TintG,
    float TintB,
    float TintAmount,
    bool IsDark,
    float RefractiveIndex,
    float Chromatic,
    float BackgroundDistance);

/// <summary>
/// 液态玻璃外壳的 CPU 渲染。
/// </summary>
/// <remarks>
/// <b>为什么不用着色器。</b>Skia 能<i>编译</i> SKSL 运行时着色器，
/// 但一<i>绘制</i>就在原生层崩（<c>sk_canvas_draw_rect</c> 抛 SEHException，
/// 连不带任何 uniform 的最简着色器也崩；同一版 libSkiaSharp 下纯色和渐变都正常）。
/// 所以这里改成在 CPU 上算。
/// <para/>
/// 对状态栏来说这反而更划算：<b>玻璃外壳不逐帧变化</b>，
/// 尺寸和参数不变时它就是一张静态图，算一次缓存起来，每帧只剩一次贴图。
/// <para/>
/// 数学取自公开的液态玻璃复刻实现（Flutter liquid_glass_renderer 的
/// shared.glsl / render.glsl），不是自己凑的观感参数：
/// <list type="bullet">
/// <item>高度剖面是半径等于厚度的四分之一圆倒角，
///       保证边缘效果<b>严格局限在厚度那么宽的一条带内</b>，
///       内部完全平整——这条是「玻璃」和「鱼眼」的分界。</item>
/// <item>法线由距离场梯度与倒角剖面合成：贴边时完全侧倒，深处朝正上。</item>
/// <item>高光是<b>两盏对向光</b>，副光 0.8 强度。这不是艺术选择，
///       macOS 26 的图层树里就是两个方向相反的高光层。</item>
/// <item>高光的空间分布是洛伦兹窄带 <c>1/(1+k·x²)</c>，不是高次幂 Phong；
///       亮度用 <c>(N·L)²</c> 而不是 <c>pow(N·L, 48)</c>，
///       出来的是一条柔和的弧而不是一个热点。</item>
/// </list>
/// </remarks>
internal static class GlassRenderer
{
    /// <summary>高光带的宽度（像素）。窄，是一条细亮线，不是一片。</summary>
    private const float RimWidth = 1.5f;

    private const float RimK = 0.89f;

    /// <summary>副光相对主光的强度。</summary>
    private const float OppositeLightScale = 0.8f;

    /// <summary>厚度不足这么多像素时不打高光，否则细条上全是白边。</summary>
    private const float MinThicknessForRim = 5f;

    /// <summary>
    /// 把玻璃外壳画进 BGRA 预乘缓冲区。
    /// </summary>
    /// <param name="pixels">目标缓冲区，长度至少 <paramref name="w"/> × <paramref name="h"/>。</param>
    public static void Render(Span<uint> pixels, int w, int h, GlassParams p,
        ReadOnlySpan<uint> backdrop = default, int backdropW = 0, int backdropH = 0,
        int backdropX = 0, int backdropY = 0)
    {
        var hasBackdrop = !backdrop.IsEmpty && backdropW > 0 && backdropH > 0;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var bx = w * 0.5f;
        var by = h * 0.5f;
        var radius = Math.Min(p.CornerRadius, Math.Min(bx, by));
        var thickness = Math.Max(p.Thickness, 0.5f);

        var lx = (float)Math.Cos(p.LightRadians);
        var ly = (float)Math.Sin(p.LightRadians);

        // 太薄的形状打高光会糊成一条白边，直接按厚度淡出。
        var thicknessFactor = Math.Clamp((thickness - MinThicknessForRim) * 0.5f, 0f, 1f);

        // 高光的颜色：深色玻璃上用白，浅色玻璃上要压暗一点，否则边缘糊掉。
        var hi = p.IsDark ? 1.0f : 0.85f;

        for (var y = 0; y < h; y++)
        {
            var py = y + 0.5f - by;

            for (var x = 0; x < w; x++)
            {
                var px = x + 0.5f - bx;

                var sd = SdRoundRect(px, py, bx, by, radius);

                // 形状外：完全透明。留一像素做抗锯齿。
                var coverage = Math.Clamp(0.5f - sd, 0f, 1f);
                if (coverage <= 0f)
                {
                    pixels[y * w + x] = 0;
                    continue;
                }

                // 倒角剖面。n_cos：贴边为 1，深入到 thickness 处降为 0。
                var nCos = Math.Clamp((thickness + sd) / thickness, 0f, 1f);
                var nSin = (float)Math.Sqrt(Math.Max(0f, 1f - nCos * nCos));

                SdGradient(px, py, bx, by, radius, out var gx, out var gy);

                // 三维法线。贴边时完全侧倒，内部朝正上方。
                var nx = gx * nCos;
                var ny = gy * nCos;
                var nz = nSin;
                var nLen = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nLen > 1e-5f)
                {
                    nx /= nLen;
                    ny /= nLen;
                    nz /= nLen;
                }

                // 洛伦兹窄带：贴着轮廓的一条细亮线。
                var xr = sd / RimWidth;
                var rimFactor = 1f / (1f + RimK * xr * xr);

                // 两盏对向光。点乘只取法线的 xy 分量。
                var mainLight = Math.Max(0f, nx * lx + ny * ly);
                var oppositeLight = Math.Max(0f, nx * -lx + ny * -ly);
                var total = mainLight + oppositeLight * OppositeLightScale;

                var directional = hi * 0.7f * (total * total) * p.LightIntensity * 2f;
                var ambient = hi * 0.4f * p.Ambient;

                // 只在倒角带里给高光，平整的内部不打。
                var shape = Math.Clamp(nCos * 1.111f, 0f, 1f);

                var rim = (directional + ambient) * rimFactor * thicknessFactor * shape;

                // 内侧暗边：紧贴亮线的里侧压一道暗，亮暗相邻才有厚度。
                var innerShade = SmoothStep(0f, 0.6f, nCos) * (1f - nCos) * 0.16f;

                var baseTint = p.TintAmount * nCos * 0.35f + p.TintAmount * 0.25f;

                float r, g, b, a;

                if (hasBackdrop)
                {
                    // ---- 折射：这一层才是液态玻璃的招牌 ----
                    // 把视线折进玻璃，再投到一块虚拟的背景平面上，取那里的颜色。
                    // 只有这一步能让边缘真正「把背后的东西掰弯」；
                    // 缺了它，倒角带里除了一条细高光什么都没有，
                    // 于是只剩「模糊 + 白边」——正是廉价复刻的样子。
                    var glassHeight = thickness * nSin;
                    Refract(nx, ny, nz, 1f / Math.Max(p.RefractiveIndex, 1.01f),
                        out var rx, out var ry, out var rz);

                    // 虚拟背景平面离玻璃多远，决定弯曲幅度。
                    //
                    // 公开实现里常见的是 8 倍厚度，但那是给「悬浮在场景之上」的玻璃用的。
                    // 状态栏是<b>贴着</b>背景的，8 倍厚度会算出上百像素的位移——
                    // 一条 56 像素高的横条，边缘会把形状之外的东西整片糊进来，
                    // 格子线不但不弯，反而被抹平。对贴合的 UI 元件，这个距离
                    // 应当和厚度同量级。
                    var baseHeight = thickness * Math.Max(p.BackgroundDistance, 0f);
                    var len = (glassHeight + baseHeight) / Math.Max(0.001f, Math.Abs(rz));

                    // 位移再夹一道，防止贴边处法线完全侧倒时算出发散的值。
                    var maxDisp = thickness * 1.6f;
                    var dx = Math.Clamp(rx * len, -maxDisp, maxDisp);
                    var dy = Math.Clamp(ry * len, -maxDisp, maxDisp);

                    // 色散：红折得比蓝多。物理上是反的，但这个方向观感更好，
                    // 公开的复刻实现都是这么做的。
                    var c = p.Chromatic;
                    SampleBackdrop(backdrop, backdropW, backdropH,
                        backdropX + x + dx * (1f + c), backdropY + y + dy * (1f + c),
                        out var r0, out _, out _);
                    SampleBackdrop(backdrop, backdropW, backdropH,
                        backdropX + x + dx, backdropY + y + dy,
                        out _, out var g0, out _);
                    SampleBackdrop(backdrop, backdropW, backdropH,
                        backdropX + x + dx * (1f - c), backdropY + y + dy * (1f - c),
                        out _, out _, out var b0);

                    r = r0 + p.TintR * baseTint + rim - innerShade;
                    g = g0 + p.TintG * baseTint + rim - innerShade;
                    b = b0 + p.TintB * baseTint + rim - innerShade;

                    // 有背景时玻璃是不透明的：它已经把背后的内容重新画了一遍。
                    a = coverage;
                }
                else
                {
                    // 没有背景可采时退化成「只有外壳」：亮边、暗边、染色。
                    // 能看，但没有折射，别指望它有多像。
                    var fringe = nCos * nCos * p.Chromatic * 0.5f;
                    r = p.TintR * baseTint + rim + fringe - innerShade;
                    g = p.TintG * baseTint + rim - innerShade;
                    b = p.TintB * baseTint + rim - fringe - innerShade;
                    a = Math.Clamp(baseTint + rim + fringe * 0.35f, 0f, 1f) * coverage;
                }

                pixels[y * w + x] = Premultiply(r, g, b, a);
            }
        }
    }

    /// <summary>圆角矩形的有向距离场（Inigo Quilez 的标准写法）。内部为负。</summary>
    private static float SdRoundRect(float px, float py, float bx, float by, float r)
    {
        var qx = Math.Abs(px) - bx + r;
        var qy = Math.Abs(py) - by + r;
        var mx = Math.Max(qx, 0f);
        var my = Math.Max(qy, 0f);
        return Math.Min(Math.Max(qx, qy), 0f) + (float)Math.Sqrt(mx * mx + my * my) - r;
    }

    /// <summary>
    /// 距离场的解析梯度，也就是单位外法线的 xy 分量。
    /// </summary>
    /// <remarks>
    /// 圆角只是把距离整体减去一个常数，<b>不改变梯度</b>，所以直接用直角矩形的梯度即可。
    /// 比中心差分省一半以上的距离场求值，而且没有采样误差。
    /// </remarks>
    private static void SdGradient(float px, float py, float bx, float by, float r,
        out float gx, out float gy)
    {
        var sx = px < 0 ? -1f : 1f;
        var sy = py < 0 ? -1f : 1f;
        var qx = Math.Abs(px) - bx + r;
        var qy = Math.Abs(py) - by + r;

        if (qx > 0f || qy > 0f)
        {
            var mx = Math.Max(qx, 0f);
            var my = Math.Max(qy, 0f);
            var len = (float)Math.Sqrt(mx * mx + my * my);
            if (len < 1e-5f)
            {
                gx = sx;
                gy = 0f;
                return;
            }

            gx = sx * mx / len;
            gy = sy * my / len;
        }
        else if (qx > qy)
        {
            gx = sx;
            gy = 0f;
        }
        else
        {
            gx = 0f;
            gy = sy;
        }
    }

    /// <summary>
    /// Snell 折射，等价于 GLSL 的 <c>refract(I, N, eta)</c>，入射方向固定为正对屏幕。
    /// </summary>
    private static void Refract(float nx, float ny, float nz, float eta,
        out float rx, out float ry, out float rz)
    {
        // 入射向量 I = (0, 0, -1)，所以 dot(N, I) = -nz。
        var dotNi = -nz;
        var k = 1f - eta * eta * (1f - dotNi * dotNi);
        if (k < 0f)
        {
            // 全反射。退化成直穿，免得出现黑洞。
            rx = 0f;
            ry = 0f;
            rz = -1f;
            return;
        }

        var f = eta * dotNi + (float)Math.Sqrt(k);
        rx = -f * nx;
        ry = -f * ny;
        rz = eta * -1f - f * nz;
    }

    /// <summary>双线性采样背景，超出范围就夹到边缘。</summary>
    private static void SampleBackdrop(ReadOnlySpan<uint> src, int sw, int sh,
        float fx, float fy, out float r, out float g, out float b)
    {
        fx = Math.Clamp(fx, 0f, sw - 1.001f);
        fy = Math.Clamp(fy, 0f, sh - 1.001f);

        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, sw - 1);
        var y1 = Math.Min(y0 + 1, sh - 1);
        var tx = fx - x0;
        var ty = fy - y0;

        Unpack(src[y0 * sw + x0], out var r00, out var g00, out var b00);
        Unpack(src[y0 * sw + x1], out var r10, out var g10, out var b10);
        Unpack(src[y1 * sw + x0], out var r01, out var g01, out var b01);
        Unpack(src[y1 * sw + x1], out var r11, out var g11, out var b11);

        r = Lerp(Lerp(r00, r10, tx), Lerp(r01, r11, tx), ty);
        g = Lerp(Lerp(g00, g10, tx), Lerp(g01, g11, tx), ty);
        b = Lerp(Lerp(b00, b10, tx), Lerp(b01, b11, tx), ty);
    }

    private static void Unpack(uint c, out float r, out float g, out float b)
    {
        r = ((c >> 16) & 0xFF) / 255f;
        g = ((c >> 8) & 0xFF) / 255f;
        b = (c & 0xFF) / 255f;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothStep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>打包成 Avalonia 要的 BGRA 预乘格式。</summary>
    private static uint Premultiply(float r, float g, float b, float a)
    {
        a = Math.Clamp(a, 0f, 1f);
        var rr = (uint)(Math.Clamp(r, 0f, 1f) * a * 255f + 0.5f);
        var gg = (uint)(Math.Clamp(g, 0f, 1f) * a * 255f + 0.5f);
        var bb = (uint)(Math.Clamp(b, 0f, 1f) * a * 255f + 0.5f);
        var aa = (uint)(a * 255f + 0.5f);
        return (aa << 24) | (rr << 16) | (gg << 8) | bb;
    }
}
