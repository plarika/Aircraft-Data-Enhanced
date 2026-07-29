// SPDX-License-Identifier: MIT
using SDRSharp.Radio;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SDRSharp.AircraftDataEnhanced;

internal sealed class SpectrumWaterfallControl : Control
{
    private readonly object _gate = new();
    private readonly Queue<float[]> _waterfallRows = new();
    private readonly int _fftSize;
    private readonly int _maxRows;
    private readonly float[] _latestSpectrum;
    private readonly double[] _tempRe;
    private readonly double[] _tempIm;
    private readonly int[] _pixelBuffer;

    private long _lastUpdateTicks;
    private long _droppedFrames;
    private int _targetFps = 8;
    private int _processing;
    private int _invalidatePending;
    private bool _paused;
    private float _minimumDb = -100f;
    private float _maximumDb = -35f;
    private float _contrast = 1.0f;
    private double _sampleRate;
    private double _filterBandwidthHz = 25_000;

    public SpectrumWaterfallControl(
        int fftSize = 256,
        int maxRows = 96)
    {
        if ((fftSize & (fftSize - 1)) != 0)
        {
            throw new ArgumentException(
                "FFT size must be a power of two.",
                nameof(fftSize));
        }

        _fftSize = fftSize;
        _maxRows = maxRows;
        _latestSpectrum = new float[_fftSize];
        _tempRe = new double[_fftSize];
        _tempIm = new double[_fftSize];

        // The buffer is resized only when the control becomes wider than this.
        // DrawWaterfall allocates a correctly sized temporary buffer when needed.
        _pixelBuffer = new int[_fftSize * _maxRows];

        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.Black;
        MinimumSize = new Size(300, 220);
    }

    public bool Paused
    {
        get
        {
            lock (_gate)
                return _paused;
        }
        set
        {
            lock (_gate)
                _paused = value;

            RequestInvalidate();
        }
    }

    public float MinimumDb
    {
        get
        {
            lock (_gate)
                return _minimumDb;
        }
        set
        {
            lock (_gate)
            {
                _minimumDb =
                    Math.Clamp(
                        value,
                        -140f,
                        -10f);

                if (_minimumDb >=
                    _maximumDb - 5f)
                {
                    _minimumDb =
                        _maximumDb - 5f;
                }
            }

            RequestInvalidate();
        }
    }

    public float MaximumDb
    {
        get
        {
            lock (_gate)
                return _maximumDb;
        }
        set
        {
            lock (_gate)
            {
                _maximumDb =
                    Math.Clamp(
                        value,
                        -120f,
                        10f);

                if (_maximumDb <=
                    _minimumDb + 5f)
                {
                    _maximumDb =
                        _minimumDb + 5f;
                }
            }

            RequestInvalidate();
        }
    }

    public float Contrast
    {
        get
        {
            lock (_gate)
                return _contrast;
        }
        set
        {
            lock (_gate)
            {
                _contrast =
                    Math.Clamp(
                        value,
                        0.25f,
                        4f);
            }

            RequestInvalidate();
        }
    }

    public double FilterBandwidthHz
    {
        get
        {
            lock (_gate)
                return _filterBandwidthHz;
        }
        set
        {
            lock (_gate)
            {
                _filterBandwidthHz =
                    Math.Max(
                        0,
                        value);
            }

            RequestInvalidate();
        }
    }

    public int TargetFps
    {
        get
        {
            lock (_gate)
                return _targetFps;
        }
        set
        {
            lock (_gate)
            {
                _targetFps =
                    Math.Clamp(
                        value,
                        2,
                        20);
            }
        }
    }

    public long DroppedFrames =>
        Interlocked.Read(
            ref _droppedFrames);

    public int RowCount
    {
        get
        {
            lock (_gate)
                return _waterfallRows.Count;
        }
    }

    public void ClearWaterfall()
    {
        lock (_gate)
        {
            _waterfallRows.Clear();
            Array.Clear(
                _latestSpectrum,
                0,
                _latestSpectrum.Length);
        }

        RequestInvalidate();
    }

    public unsafe void PushIq(
        Complex* buffer,
        int length,
        double sampleRate)
    {
        if (buffer is null ||
            length < _fftSize ||
            IsDisposed)
        {
            return;
        }

        bool paused;
        int fps;

        lock (_gate)
        {
            paused = _paused;
            fps = _targetFps;
        }

        if (paused)
            return;

        var now =
            Environment.TickCount64;

        var interval =
            Math.Max(
                1,
                1000 / fps);

        if (now -
            Interlocked.Read(
                ref _lastUpdateTicks) <
            interval)
        {
            return;
        }

        if (Interlocked.Exchange(
            ref _processing,
            1) != 0)
        {
            Interlocked.Increment(
                ref _droppedFrames);
            return;
        }

        try
        {
            Interlocked.Exchange(
                ref _lastUpdateTicks,
                now);

            var start =
                Math.Max(
                    0,
                    (length - _fftSize) / 2);

            for (var n = 0;
                 n < _fftSize;
                 n++)
            {
                var sample =
                    buffer[start + n];

                var window =
                    0.5 -
                    0.5 *
                    Math.Cos(
                        2.0 *
                        Math.PI *
                        n /
                        (_fftSize - 1));

                _tempRe[n] =
                    sample.Real *
                    window;

                _tempIm[n] =
                    sample.Imag *
                    window;
            }

            FftInPlace(
                _tempRe,
                _tempIm);

            var scale =
                1.0 /
                (_fftSize *
                 _fftSize);

            var row =
                new float[_fftSize];

            for (var k = 0;
                 k < _fftSize;
                 k++)
            {
                var shifted =
                    (k + _fftSize / 2) %
                    _fftSize;

                var power =
                    (
                        _tempRe[shifted] *
                        _tempRe[shifted]
                        +
                        _tempIm[shifted] *
                        _tempIm[shifted]
                    ) *
                    scale;

                row[k] =
                    (float)(
                        10.0 *
                        Math.Log10(
                            Math.Max(
                                power,
                                1e-20)));
            }

            lock (_gate)
            {
                _sampleRate =
                    sampleRate;

                Array.Copy(
                    row,
                    _latestSpectrum,
                    row.Length);

                _waterfallRows.Enqueue(
                    row);

                while (_waterfallRows.Count >
                       _maxRows)
                {
                    _waterfallRows.Dequeue();
                }
            }

            RequestInvalidate();
        }
        catch
        {
            Interlocked.Increment(
                ref _droppedFrames);
        }
        finally
        {
            Volatile.Write(
                ref _processing,
                0);
        }
    }

    protected override void OnPaint(
        PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode =
            SmoothingMode.None;

        e.Graphics.Clear(
            Color.Black);

        float[] spectrum;
        float[][] rows;
        float minimumDb;
        float maximumDb;
        float contrast;
        double sampleRate;
        double filterBandwidth;
        bool paused;

        lock (_gate)
        {
            spectrum =
                (float[])_latestSpectrum.Clone();

            // Rows are immutable after being queued, so only the queue
            // container needs to be copied.
            rows =
                _waterfallRows.ToArray();

            minimumDb =
                _minimumDb;

            maximumDb =
                _maximumDb;

            contrast =
                _contrast;

            sampleRate =
                _sampleRate;

            filterBandwidth =
                _filterBandwidthHz;

            paused =
                _paused;
        }

        var spectrumHeight =
            Math.Max(
                90,
                Height / 3);

        var waterfallTop =
            spectrumHeight + 1;

        var waterfallHeight =
            Math.Max(
                1,
                Height - waterfallTop);

        DrawSpectrum(
            e.Graphics,
            spectrum,
            spectrumHeight,
            minimumDb,
            maximumDb);

        DrawWaterfall(
            e.Graphics,
            rows,
            waterfallTop,
            waterfallHeight,
            minimumDb,
            maximumDb,
            contrast);

        DrawMarkers(
            e.Graphics,
            spectrumHeight,
            sampleRate,
            filterBandwidth);

        DrawLabels(
            e.Graphics,
            spectrumHeight,
            minimumDb,
            maximumDb,
            paused);
    }

    private void RequestInvalidate()
    {
        if (IsDisposed ||
            Disposing ||
            !IsHandleCreated)
        {
            return;
        }

        // At most one UI callback may be pending. This prevents an SDR IQ
        // callback from flooding the WinForms message queue when painting is
        // temporarily slow.
        if (Interlocked.Exchange(
            ref _invalidatePending,
            1) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke(
                (Action)(() =>
                {
                    Interlocked.Exchange(
                        ref _invalidatePending,
                        0);

                    if (!IsDisposed &&
                        !Disposing)
                    {
                        Invalidate();
                    }
                }));
        }
        catch (
            InvalidOperationException)
        {
            Interlocked.Exchange(
                ref _invalidatePending,
                0);
        }
    }

    private void DrawSpectrum(
        Graphics graphics,
        float[] spectrum,
        int height,
        float minimumDb,
        float maximumDb)
    {
        using var gridPen =
            new Pen(
                Color.FromArgb(
                    45,
                    255,
                    255,
                    255));

        for (var x = 0;
             x < Width;
             x += Math.Max(
                 1,
                 Width / 8))
        {
            graphics.DrawLine(
                gridPen,
                x,
                0,
                x,
                height);
        }

        for (var y = 0;
             y < height;
             y += Math.Max(
                 1,
                 height / 4))
        {
            graphics.DrawLine(
                gridPen,
                0,
                y,
                Width,
                y);
        }

        if (spectrum.Length < 2)
            return;

        var points =
            new PointF[
                spectrum.Length];

        for (var index = 0;
             index < spectrum.Length;
             index++)
        {
            var normalized =
                NormalizeDb(
                    spectrum[index],
                    minimumDb,
                    maximumDb);

            var x =
                index *
                (Width - 1f) /
                (spectrum.Length - 1f);

            var y =
                (1f - normalized) *
                (height - 1f);

            points[index] =
                new PointF(
                    x,
                    y);
        }

        using var pen =
            new Pen(
                Color.Lime,
                1.2f);

        graphics.DrawLines(
            pen,
            points);
    }

    private void DrawWaterfall(
        Graphics graphics,
        float[][] rows,
        int top,
        int height,
        float minimumDb,
        float maximumDb,
        float contrast)
    {
        if (rows.Length == 0 ||
            Width <= 0)
        {
            return;
        }

        var bitmapWidth =
            Math.Max(
                1,
                Width);

        var bitmapHeight =
            Math.Max(
                1,
                rows.Length);

        using var bitmap =
            new Bitmap(
                bitmapWidth,
                bitmapHeight,
                PixelFormat.Format32bppPArgb);

        var requiredPixels =
            checked(
                bitmapWidth *
                bitmapHeight);

        var pixels =
            requiredPixels <=
            _pixelBuffer.Length
                ? _pixelBuffer
                : new int[
                    requiredPixels];

        for (var rowIndex = 0;
             rowIndex < rows.Length;
             rowIndex++)
        {
            var row =
                rows[rowIndex];

            var destination =
                (rows.Length - 1 - rowIndex) *
                bitmapWidth;

            for (var x = 0;
                 x < bitmapWidth;
                 x++)
            {
                var sourceIndex =
                    Math.Min(
                        row.Length - 1,
                        x *
                        row.Length /
                        bitmapWidth);

                var normalized =
                    NormalizeDb(
                        row[sourceIndex],
                        minimumDb,
                        maximumDb);

                normalized =
                    MathF.Pow(
                        normalized,
                        1f / contrast);

                pixels[
                    destination + x] =
                    ToHeatArgb(
                        normalized);
            }
        }

        var bounds =
            new Rectangle(
                0,
                0,
                bitmapWidth,
                bitmapHeight);

        var data =
            bitmap.LockBits(
                bounds,
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);

        try
        {
            Marshal.Copy(
                pixels,
                0,
                data.Scan0,
                requiredPixels);
        }
        finally
        {
            bitmap.UnlockBits(
                data);
        }

        var rowHeight =
            Math.Max(
                1f,
                height /
                (float)_maxRows);

        var startY =
            top +
            height -
            rows.Length *
            rowHeight;

        graphics.InterpolationMode =
            InterpolationMode.NearestNeighbor;

        graphics.PixelOffsetMode =
            PixelOffsetMode.Half;

        graphics.DrawImage(
            bitmap,
            new RectangleF(
                0,
                startY,
                Width,
                rows.Length *
                rowHeight),
            new RectangleF(
                0,
                0,
                bitmap.Width,
                bitmap.Height),
            GraphicsUnit.Pixel);
    }

    private void DrawMarkers(
        Graphics graphics,
        int spectrumHeight,
        double sampleRate,
        double filterBandwidth)
    {
        _ = spectrumHeight;

        using var centerPen =
            new Pen(
                Color.White,
                1f)
            {
                DashStyle =
                    DashStyle.Dash
            };

        var centerX =
            Width / 2f;

        graphics.DrawLine(
            centerPen,
            centerX,
            0,
            centerX,
            Height);

        if (sampleRate <= 0 ||
            filterBandwidth <= 0)
        {
            return;
        }

        var halfWidthPixels =
            (float)(
                filterBandwidth /
                sampleRate *
                Width /
                2.0);

        using var filterPen =
            new Pen(
                Color.Yellow,
                1f)
            {
                DashStyle =
                    DashStyle.Dot
            };

        graphics.DrawLine(
            filterPen,
            centerX -
                halfWidthPixels,
            0,
            centerX -
                halfWidthPixels,
            Height);

        graphics.DrawLine(
            filterPen,
            centerX +
                halfWidthPixels,
            0,
            centerX +
                halfWidthPixels,
            Height);
    }

    private void DrawLabels(
        Graphics graphics,
        int spectrumHeight,
        float minimumDb,
        float maximumDb,
        bool paused)
    {
        using var brush =
            new SolidBrush(
                Color.White);

        using var font =
            new Font(
                Font.FontFamily,
                Math.Max(
                    7f,
                    Font.Size - 1f));

        graphics.DrawString(
            "Spectrum",
            font,
            brush,
            4,
            4);

        graphics.DrawString(
            "Waterfall",
            font,
            brush,
            4,
            spectrumHeight + 4);

        graphics.DrawString(
            $"{maximumDb:0} dBFS",
            font,
            brush,
            4,
            20);

        graphics.DrawString(
            $"{minimumDb:0} dBFS",
            font,
            brush,
            4,
            spectrumHeight - 18);

        if (paused)
        {
            using var pausedBrush =
                new SolidBrush(
                    Color.Yellow);

            const string text =
                "PAUSED";

            var size =
                graphics.MeasureString(
                    text,
                    font);

            graphics.DrawString(
                text,
                font,
                pausedBrush,
                Math.Max(
                    4,
                    Width -
                    size.Width -
                    6),
                4);
        }
    }

    private static float NormalizeDb(
        float value,
        float minimumDb,
        float maximumDb)
    {
        var span =
            Math.Max(
                5f,
                maximumDb -
                minimumDb);

        return Math.Clamp(
            (value -
             minimumDb) /
            span,
            0f,
            1f);
    }

    private static int ToHeatArgb(
        float value)
    {
        value =
            Math.Clamp(
                value,
                0f,
                1f);

        int red;
        int green;
        int blue;

        if (value < 0.20f)
        {
            var scale =
                value /
                0.20f;

            red = 0;
            green = 0;
            blue =
                (int)(
                    30 +
                    100 *
                    scale);
        }
        else if (value < 0.45f)
        {
            var scale =
                (value -
                 0.20f) /
                0.25f;

            red = 0;
            green =
                (int)(
                    180 *
                    scale);

            blue =
                (int)(
                    130 +
                    125 *
                    scale);
        }
        else if (value < 0.70f)
        {
            var scale =
                (value -
                 0.45f) /
                0.25f;

            red =
                (int)(
                    255 *
                    scale);

            green =
                180 +
                (int)(
                    75 *
                    scale);

            blue =
                (int)(
                    255 *
                    (1 -
                     scale));
        }
        else
        {
            var scale =
                (value -
                 0.70f) /
                0.30f;

            red = 255;
            green =
                (int)(
                    255 *
                    (1 -
                     scale));

            blue = 0;
        }

        return unchecked(
            (int)(
                0xFF000000u |
                ((uint)red << 16) |
                ((uint)green << 8) |
                (uint)blue));
    }

    private static void FftInPlace(
        double[] real,
        double[] imaginary)
    {
        var length =
            real.Length;

        for (int index = 1,
             reversed = 0;
             index < length;
             index++)
        {
            var bit =
                length >> 1;

            for (;
                 (reversed & bit) != 0;
                 bit >>= 1)
            {
                reversed ^= bit;
            }

            reversed ^= bit;

            if (index >= reversed)
                continue;

            (
                real[index],
                real[reversed]
            ) =
            (
                real[reversed],
                real[index]
            );

            (
                imaginary[index],
                imaginary[reversed]
            ) =
            (
                imaginary[reversed],
                imaginary[index]
            );
        }

        for (var stageLength = 2;
             stageLength <= length;
             stageLength <<= 1)
        {
            var angle =
                -2.0 *
                Math.PI /
                stageLength;

            var stepReal =
                Math.Cos(
                    angle);

            var stepImaginary =
                Math.Sin(
                    angle);

            for (var offset = 0;
                 offset < length;
                 offset += stageLength)
            {
                var twiddleReal =
                    1.0;

                var twiddleImaginary =
                    0.0;

                for (var element = 0;
                     element <
                         stageLength / 2;
                     element++)
                {
                    var evenReal =
                        real[
                            offset +
                            element];

                    var evenImaginary =
                        imaginary[
                            offset +
                            element];

                    var oddIndex =
                        offset +
                        element +
                        stageLength / 2;

                    var oddReal =
                        real[oddIndex] *
                        twiddleReal
                        -
                        imaginary[oddIndex] *
                        twiddleImaginary;

                    var oddImaginary =
                        real[oddIndex] *
                        twiddleImaginary
                        +
                        imaginary[oddIndex] *
                        twiddleReal;

                    real[
                        offset +
                        element] =
                        evenReal +
                        oddReal;

                    imaginary[
                        offset +
                        element] =
                        evenImaginary +
                        oddImaginary;

                    real[oddIndex] =
                        evenReal -
                        oddReal;

                    imaginary[oddIndex] =
                        evenImaginary -
                        oddImaginary;

                    var nextReal =
                        twiddleReal *
                        stepReal
                        -
                        twiddleImaginary *
                        stepImaginary;

                    twiddleImaginary =
                        twiddleReal *
                        stepImaginary
                        +
                        twiddleImaginary *
                        stepReal;

                    twiddleReal =
                        nextReal;
                }
            }
        }
    }
}
