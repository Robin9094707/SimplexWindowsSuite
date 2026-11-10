using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SimplexSuite.Core.Audio;
using SimplexSuite.Core.Audio.Effects;
using SimplexSuite.Core.Dsp;

namespace SimplexSuite.Pages;

public sealed partial class PodcastPage
{
    private sealed record P(string Label, double Min, double Max, double Value, double Step, string Unit = "", string? Description = null, string[]? Options = null);

    private void BuildEffects()
    {
        // --- Rauschen & Reinigung
        Section("Reinigung");
        var profileText = new TextBlock { Text = "Kein Profil gelernt", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 150 };
        var learn = new Button { Content = "Aus Auswahl lernen" };
        learn.Click += (_, _) =>
        {
            if (_doc == null || !Wave.HasSelection) { Ui.Success("Hinweis", "Markiere zuerst einen Bereich, in dem nur Rauschen zu hören ist."); return; }
            var (s, l) = TargetRange();
            _noiseProfile = NoiseProfile.Learn(_doc.GetRange(s, l), _doc.Channels, _doc.SampleRate);
            profileText.Text = $"Profil aus {(l / (double)_doc.SampleRate):0.0} s gelernt";
            Ui.Success("Rauschprofil gelernt", "Entferne jetzt die Auswahl (oder wähle alles) und wende den Effekt an.");
        };
        var learnCard = new SettingsCard { Header = "Rauschprofil", Description = "Bereich mit reinem Rauschen markieren", Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { profileText, learn } } };
        AddEffect("Rauschunterdrückung (spektral)", "Entfernt gleichmäßiges Rauschen, Brummen, Lüfter – mit gelerntem Profil oder automatisch.", "",
            new[]
            {
                new P("Profil", 0, 1, 0, 1, Options: new[] { "Gelerntes Profil (falls vorhanden)", "Automatisch schätzen" }),
                new P("Reduktion", 0, 48, 18, 0.5, "dB"),
                new P("Empfindlichkeit", -6, 24, 6, 0.5, "dB", "Abstand über dem Rauschboden, ab dem Signal durchgelassen wird"),
                new P("Frequenzglättung", 0, 12, 3, 1, "Bänder"),
                new P("Ausklingzeit", 10, 600, 120, 5, "ms"),
            },
            v => new SpectralNoiseReduction(v[0] < 0.5 ? _noiseProfile : null, v[1], v[2], (int)v[3], v[4]), learnCard);

        AddEffect("KI-Rauschunterdrückung (RNNoise)", "Neuronales Netz für Sprache – entfernt auch nicht-stationäre Geräusche (Tastatur, Verkehr).", "",
            new[] { new P("Stärke (Mix)", 0, 1, 1, 0.05) },
            v => FfmpegFilterEffect.RnNoise(v[0]));

        AddEffect("FFT-Entrauschen", "Adaptive Rauschminderung mit Rausch-Tracking (FFmpeg afftdn).", "",
            new[]
            {
                new P("Reduktion", 1, 60, 12, 1, "dB"),
                new P("Rauschboden", -80, -20, -50, 1, "dB"),
                new P("Rauschen verfolgen", 0, 1, 1, 1, Options: new[] { "Aus", "Ein" }),
            },
            v => FfmpegFilterEffect.FftDenoise(v[0], v[1], v[2] > 0.5));

        AddEffect("Noise Gate", "Schließt in Sprechpausen und senkt Hintergrundgeräusche ab.", "",
            new[]
            {
                new P("Schwelle", -90, 0, -45, 0.5, "dB"),
                new P("Absenkung", 0, 90, 24, 1, "dB"),
                new P("Attack", 0.1, 100, 2, 0.1, "ms"),
                new P("Hold", 0, 1000, 80, 5, "ms"),
                new P("Release", 5, 2000, 150, 5, "ms"),
            },
            v => new NoiseGateEffect(v[0], v[1], v[2], v[3], v[4]));

        AddEffect("De-Esser", "Zähmt scharfe S- und Zischlaute.", "",
            new[]
            {
                new P("Frequenz", 2000, 12000, 5500, 50, "Hz"),
                new P("Schwelle", -60, 0, -30, 0.5, "dB"),
                new P("Max. Reduktion", 0, 24, 10, 0.5, "dB"),
            },
            v => new DeEsserEffect(v[0], v[1], v[2]));

        AddEffect("Pausen kürzen", "Verkürzt lange Sprechpausen automatisch auf eine Wunschlänge.", "",
            new[]
            {
                new P("Stilleschwelle", -80, -10, -45, 0.5, "dB"),
                new P("Pausen ab", 100, 5000, 800, 10, "ms"),
                new P("Pause behalten", 0, 3000, 400, 10, "ms"),
            },
            v => new TruncateSilenceEffect(v[0], v[1], v[2]), preview: false);

        // --- Klang
        Section("Klang");
        BuildEqualizer();
        AddEffect("Kompressor", "Gleicht Lautstärkeunterschiede aus – mehr Präsenz und Durchsetzung.", "",
            new[]
            {
                new P("Schwelle", -60, 0, -18, 0.5, "dB"),
                new P("Ratio", 1, 20, 3, 0.1, ": 1"),
                new P("Attack", 0.1, 200, 10, 0.1, "ms"),
                new P("Release", 5, 2000, 150, 5, "ms"),
                new P("Knie", 0, 24, 6, 0.5, "dB"),
                new P("Aufholverstärkung", 0, 24, 0, 0.5, "dB"),
                new P("Auto-Aufholung", 0, 1, 0, 1, Options: new[] { "Aus", "Ein" }),
            },
            v => new CompressorEffect(v[0], v[1], v[2], v[3], v[4], v[5], v[6] > 0.5));

        AddEffect("Limiter", "Verhindert Übersteuerung mit Look-Ahead – ohne Verzerrung.", "",
            new[]
            {
                new P("Obergrenze", -12, 0, -1, 0.1, "dBFS"),
                new P("Release", 1, 1000, 80, 1, "ms"),
                new P("Look-Ahead", 1, 20, 5, 0.5, "ms"),
            },
            v => new LimiterEffect(v[0], v[1], v[2]));

        AddEffect("Hall", "Raumklang (Freeverb) – von kleinem Zimmer bis Kathedrale.", "",
            new[]
            {
                new P("Raumgröße", 0, 1, 0.5, 0.01),
                new P("Dämpfung", 0, 1, 0.5, 0.01),
                new P("Hallanteil", 0, 1, 0.25, 0.01),
                new P("Direktsignal", 0, 1, 1, 0.01),
                new P("Stereobreite", 0, 1, 1, 0.01),
                new P("Pre-Delay", 0, 200, 10, 1, "ms"),
            },
            v => new ReverbEffect(v[0], v[1], v[2], v[3], v[4], v[5]));

        AddEffect("Echo", "Verzögerung mit Rückkopplung.", "",
            new[]
            {
                new P("Verzögerung", 10, 2000, 300, 5, "ms"),
                new P("Rückkopplung", 0, 0.95, 0.35, 0.01),
                new P("Anteil", 0, 1, 0.4, 0.01),
            },
            v => new EchoEffect(v[0], v[1], v[2]));

        AddEffect("Tempo", "Sprechtempo ändern, ohne die Tonhöhe zu verändern.", "",
            new[] { new P("Faktor", 0.5, 2.0, 1.0, 0.01, "×") },
            v => FfmpegFilterEffect.Tempo(v[0]));

        AddEffect("Tonhöhe", "Stimme höher oder tiefer – Länge bleibt gleich.", "",
            new[] { new P("Halbtöne", -12, 12, 0, 0.1, "HT") },
            v => FfmpegFilterEffect.Pitch(v[0], _doc?.SampleRate ?? 48000));

        // --- Pegel
        Section("Pegel");
        AddEffect("Verstärkung", "Lautstärke um einen festen Wert ändern.", "",
            new[] { new P("Verstärkung", -40, 40, 0, 0.1, "dB") },
            v => new GainEffect(v[0]));

        AddEffect("Normalisieren", "Spitzenpegel auf einen Zielwert bringen.", "",
            new[]
            {
                new P("Spitzenpegel", -24, 0, -1, 0.1, "dBFS"),
                new P("Gleichspannung entfernen", 0, 1, 1, 1, Options: new[] { "Nein", "Ja" }),
                new P("Kanäle getrennt", 0, 1, 0, 1, Options: new[] { "Nein", "Ja" }),
            },
            v => new NormalizeEffect(v[0], v[1] > 0.5, v[2] > 0.5));

        AddEffect("Lautheit (LUFS)", "Normalisiert auf eine Ziel-Lautheit nach EBU R128 – z. B. −16 LUFS für Podcasts.", "",
            new[]
            {
                new P("Ziel-Lautheit", -36, -6, -16, 0.5, "LUFS"),
                new P("True-Peak-Grenze", -6, 0, -1, 0.1, "dBTP"),
            },
            v => new LoudnessEffect(v[0], v[1]));

        AddEffect("Ein-/Ausblenden", "Weiche Blende über die Auswahl.", "",
            new[]
            {
                new P("Richtung", 0, 1, 0, 1, Options: new[] { "Einblenden", "Ausblenden" }),
                new P("Kurve", 0, 3, 3, 1, Options: new[] { "Linear", "Logarithmisch", "Exponentiell", "S-Kurve" }),
            },
            v => new FadeEffect(v[0] < 0.5, (FadeCurve)(int)v[1]));

        // --- Werkzeuge
        Section("Werkzeuge");
        var tools = new SettingsExpander { Header = "Werkzeuge", Description = "Umkehren, invertieren, Kanäle …", HeaderIcon = Ui.Icon("") };
        foreach (var (mode, label) in new[]
                 {
                     (UtilityMode.Reverse, "Rückwärts abspielen"), (UtilityMode.Invert, "Phase invertieren"),
                     (UtilityMode.SwapChannels, "Links/Rechts tauschen"), (UtilityMode.MonoMix, "Zu Mono mischen"),
                     (UtilityMode.RemoveDc, "Gleichspannung entfernen"),
                 })
        {
            var b = new Button { Content = "Anwenden" };
            var m = mode;
            b.Click += async (_, _) => await ApplyEffectAsync(new UtilityEffect(m));
            tools.Items.Add(new SettingsCard { Header = label, Content = b });
        }
        EffectsHost.Children.Add(tools);

        var custom = new SettingsExpander { Header = "Eigener FFmpeg-Filter", Description = "Für Profis: jede FFmpeg-Audiofilterkette, z. B. „highpass=f=80,acompressor“", HeaderIcon = Ui.Icon("") };
        var filterBox = new TextBox { PlaceholderText = "z. B. highpass=f=80,lowpass=f=12000", AcceptsReturn = false, MinWidth = 260 };
        var cApply = new Button { Content = "Anwenden", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var cPreview = new Button { Content = "Vorhören" };
        cApply.Click += async (_, _) => { if (filterBox.Text.Trim().Length > 0) await ApplyEffectAsync(new FfmpegFilterEffect("FFmpeg: " + filterBox.Text.Trim(), filterBox.Text.Trim())); };
        cPreview.Click += async (_, _) => { if (filterBox.Text.Trim().Length > 0) await PreviewEffectAsync(new FfmpegFilterEffect("Vorschau", filterBox.Text.Trim())); };
        custom.Items.Add(new SettingsCard { ContentAlignment = ContentAlignment.Left, Content = filterBox });
        custom.Items.Add(new SettingsCard { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cPreview, cApply } } });
        EffectsHost.Children.Add(custom);
    }

    private void Section(string title)
    {
        EffectsHost.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(2, 14, 0, 4) });
    }

    private void AddEffect(string title, string description, string glyph, P[] ps, Func<double[], IAudioEffect> factory, FrameworkElement? extra = null, bool preview = true)
    {
        var values = ps.Select(p => p.Value).ToArray();
        var exp = new SettingsExpander { Header = title, Description = description, HeaderIcon = Ui.Icon(glyph) };
        if (extra != null) exp.Items.Add(extra);
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            int idx = i;
            var card = new SettingsCard { Header = p.Label, Description = p.Description };
            if (p.Options != null)
            {
                var cb = new ComboBox { MinWidth = 180 };
                foreach (var o in p.Options) cb.Items.Add(o);
                cb.SelectedIndex = (int)p.Value;
                cb.SelectionChanged += (_, _) => values[idx] = cb.SelectedIndex;
                card.Content = cb;
            }
            else
            {
                var slider = new Slider { Minimum = p.Min, Maximum = p.Max, StepFrequency = p.Step, Value = p.Value, Width = 130, VerticalAlignment = VerticalAlignment.Center };
                var nb = new NumberBox
                {
                    Minimum = p.Min, Maximum = p.Max, Value = p.Value, SmallChange = p.Step, LargeChange = p.Step * 10,
                    Width = 92, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                    NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
                    {
                        FractionDigits = p.Step >= 1 ? 0 : (p.Step >= 0.1 ? 1 : 2),
                        NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = p.Step },
                    },
                };
                bool sync = false;
                slider.ValueChanged += (_, e) =>
                {
                    if (sync) return;
                    sync = true; nb.Value = e.NewValue; values[idx] = e.NewValue; sync = false;
                };
                nb.ValueChanged += (_, e) =>
                {
                    if (sync || double.IsNaN(e.NewValue)) return;
                    sync = true; slider.Value = e.NewValue; values[idx] = e.NewValue; sync = false;
                };
                var unit = new TextBlock { Text = p.Unit, VerticalAlignment = VerticalAlignment.Center, MinWidth = 28, Foreground = Ui.ThemeBrush("TextFillColorSecondaryBrush") };
                card.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { slider, nb, unit } };
            }
            exp.Items.Add(card);
        }
        var apply = new Button { Content = "Anwenden", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        apply.Click += async (_, _) => await ApplyEffectAsync(factory(values));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (preview)
        {
            var pv = new Button { Content = "Vorhören" };
            pv.Click += async (_, _) => await PreviewEffectAsync(factory(values));
            buttons.Children.Add(pv);
        }
        buttons.Children.Add(apply);
        exp.Items.Add(new SettingsCard { Content = buttons });
        EffectsHost.Children.Add(exp);
    }

    private async Task ApplyEffectAsync(IAudioEffect fx)
    {
        if (_doc == null) { Ui.Success("Keine Datei", "Öffne zuerst eine Audiodatei."); return; }
        var (s, l) = TargetRange();
        if (l <= 0) return;
        bool hadSelection = Wave.HasSelection;
        var data = _doc.GetRange(s, l);
        await RunBusyAsync(fx.Name, async (p, ct) =>
        {
            var result = await fx.ProcessAsync(data, _doc.Channels, _doc.SampleRate, p, ct);
            _doc.Replace(s, l, result, fx.Name);
            if (hadSelection) Wave.SetSelection(_doc.ToSeconds(s), _doc.ToSeconds(s + result.LongLength / _doc.Channels));
        });
    }

    private async Task PreviewEffectAsync(IAudioEffect fx)
    {
        if (_doc == null) return;
        long s, l;
        if (Wave.HasSelection)
        {
            (s, l) = TargetRange();
            l = Math.Min(l, _doc.SampleRate * 20L);
        }
        else
        {
            s = _doc.ToFrame(Wave.Cursor);
            l = Math.Min(_doc.SampleRate * 8L, _doc.Frames - s);
        }
        if (l <= 0) return;
        var data = _doc.GetRange(s, l);
        await RunBusyAsync("Vorschau: " + fx.Name, async (p, ct) =>
        {
            var result = await fx.ProcessAsync(data, _doc.Channels, _doc.SampleRate, p, ct);
            PlayBuffer(result, _doc.ToSeconds(s));
        });
    }

    // ------------------------------------------------------------------ Equalizer

    private void BuildEqualizer()
    {
        var bands = new List<EqBand>
        {
            new() { Type = FilterType.LowCut, Frequency = 80, Q = 0.707 },
            new() { Type = FilterType.Peak, Frequency = 250, GainDb = 0, Q = 1 },
            new() { Type = FilterType.Peak, Frequency = 3000, GainDb = 0, Q = 1 },
            new() { Type = FilterType.HighShelf, Frequency = 10000, GainDb = 0, Q = 0.707 },
        };
        double outGain = 0;
        var exp = new SettingsExpander { Header = "Equalizer", Description = "Parametrischer EQ mit beliebig vielen Bändern und Frequenzkurve.", HeaderIcon = Ui.Icon("") };
        var canvas = new Canvas { Height = 130, Width = 330 };
        var curve = new Polyline { Stroke = Ui.ThemeBrush("AccentFillColorDefaultBrush"), StrokeThickness = 2 };
        var bandsHost = new StackPanel { Spacing = 6 };
        double FreqToX(double f) => Math.Log10(f / 20) / Math.Log10(1000) * canvas.Width;
        double DbToY(double db) => canvas.Height / 2 - db / 24 * (canvas.Height / 2);

        void DrawCurve()
        {
            canvas.Children.Clear();
            var grid = Ui.ThemeBrush("DividerStrokeColorDefaultBrush");
            foreach (var f in new[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
            {
                var l = new Line { X1 = FreqToX(f), X2 = FreqToX(f), Y1 = 0, Y2 = canvas.Height, Stroke = grid, StrokeThickness = 1 };
                canvas.Children.Add(l);
                var t = new TextBlock { Text = f >= 1000 ? $"{f / 1000}k" : f.ToString(), FontSize = 10, Foreground = Ui.ThemeBrush("TextFillColorTertiaryBrush") };
                Canvas.SetLeft(t, FreqToX(f) + 2); Canvas.SetTop(t, canvas.Height - 14);
                canvas.Children.Add(t);
            }
            foreach (var db in new[] { -12, 0, 12 })
                canvas.Children.Add(new Line { X1 = 0, X2 = canvas.Width, Y1 = DbToY(db), Y2 = DbToY(db), Stroke = grid, StrokeThickness = db == 0 ? 1.5 : 0.5 });
            var pts = new PointCollection();
            int sr = _doc?.SampleRate ?? 48000;
            for (int x = 0; x <= (int)canvas.Width; x += 2)
            {
                double f = 20 * Math.Pow(1000, x / canvas.Width);
                double db = EqualizerEffect.ResponseDb(bands, f, sr) + outGain;
                pts.Add(new Windows.Foundation.Point(x, Math.Clamp(DbToY(db), 0, canvas.Height)));
            }
            curve.Points = pts;
            canvas.Children.Add(curve);
        }

        void RebuildBands()
        {
            bandsHost.Children.Clear();
            foreach (var band in bands.ToList())
            {
                var b = band;
                var type = new ComboBox { Width = 120 };
                foreach (var n in new[] { "Low Cut", "High Cut", "Glocke", "Low Shelf", "High Shelf", "Notch", "Bandpass" }) type.Items.Add(n);
                type.SelectedIndex = (int)b.Type;
                var freq = new NumberBox { Header = "Hz", Minimum = 10, Maximum = 22000, Value = b.Frequency, Width = 84, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
                var gain = new NumberBox { Header = "dB", Minimum = -24, Maximum = 24, Value = b.GainDb, Width = 64, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
                var q = new NumberBox { Header = "Q", Minimum = 0.1, Maximum = 18, Value = b.Q, Width = 56, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
                var on = new CheckBox { IsChecked = b.Enabled, MinWidth = 0, VerticalAlignment = VerticalAlignment.Bottom };
                var del = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, VerticalAlignment = VerticalAlignment.Bottom };
                type.SelectionChanged += (_, _) => { b.Type = (FilterType)type.SelectedIndex; DrawCurve(); };
                freq.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { b.Frequency = e.NewValue; DrawCurve(); } };
                gain.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { b.GainDb = e.NewValue; DrawCurve(); } };
                q.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { b.Q = e.NewValue; DrawCurve(); } };
                on.Click += (_, _) => { b.Enabled = on.IsChecked == true; DrawCurve(); };
                del.Click += (_, _) => { bands.Remove(b); RebuildBands(); DrawCurve(); };
                var typeStack = new StackPanel { Children = { new TextBlock { Text = "Typ", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) }, type } };
                bandsHost.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { on, typeStack, freq, gain, q, del } });
            }
        }

        var add = new Button { Content = "+ Band" };
        add.Click += (_, _) => { bands.Add(new EqBand { Type = FilterType.Peak, Frequency = 1000, Q = 1 }); RebuildBands(); DrawCurve(); };
        var outBox = new NumberBox { Header = "Ausgang dB", Minimum = -24, Maximum = 24, Value = 0, Width = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        outBox.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) { outGain = e.NewValue; DrawCurve(); } };
        var apply = new Button { Content = "Anwenden", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var preview = new Button { Content = "Vorhören" };
        apply.Click += async (_, _) => await ApplyEffectAsync(new EqualizerEffect(bands.Select(Copy).ToList(), outGain));
        preview.Click += async (_, _) => await PreviewEffectAsync(new EqualizerEffect(bands.Select(Copy).ToList(), outGain));

        exp.Items.Add(new SettingsCard { ContentAlignment = ContentAlignment.Left, Content = canvas });
        exp.Items.Add(new SettingsCard { ContentAlignment = ContentAlignment.Left, Content = bandsHost });
        exp.Items.Add(new SettingsCard { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, outBox, preview, apply } } });
        RebuildBands();
        DrawCurve();
        EffectsHost.Children.Add(exp);
    }

    private static EqBand Copy(EqBand b) => new() { Type = b.Type, Frequency = b.Frequency, GainDb = b.GainDb, Q = b.Q, Enabled = b.Enabled };
}
