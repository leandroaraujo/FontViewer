namespace FontViewer;

public partial class MainPage : ContentPage
{
	public static readonly BindableProperty FontIconSizeProperty =
		BindableProperty.Create(nameof(FontIconSize), typeof(double), typeof(MainPage), 32.0);

	public double FontIconSize
	{
		get => (double)GetValue(FontIconSizeProperty);
		set => SetValue(FontIconSizeProperty, value);
	}

	public static readonly BindableProperty IconFontFamilyProperty =
		BindableProperty.Create(nameof(IconFontFamily), typeof(string), typeof(MainPage), "SegoeFluentIcons");

	public string IconFontFamily
	{
		get => (string)GetValue(IconFontFamilyProperty);
		set => SetValue(IconFontFamilyProperty, value);
	}

	private readonly Dictionary<string, string> _fonts = new()
	{
		["Segoe Fluent Icons"] = "SegoeFluentIcons",
		["Font Awesome 5 Regular"] = "FontAwesome5Regular",
		["Material Symbols Outlined"] = "MaterialSymbolsOutlined",
		["Fluent System Icons Filled"] = "FluentSystemIconsFilled",
	};

	private readonly Dictionary<string, string> _fontFiles = new()
	{
		["SegoeFluentIcons"] = "segoe-fluent-icons.ttf",
		["FontAwesome5Regular"] = "fontawesome-5-free-regular-400.ttf",
		["MaterialSymbolsOutlined"] = "MaterialSymbolsOutlined.ttf",
		["FluentSystemIconsFilled"] = "FluentSystemIcons-Filled.ttf",
	};

	public MainPage()
	{
		InitializeComponent();
		FontPicker.ItemsSource = _fonts.Keys.ToList();
		FontPicker.SelectedIndex = 0;
	}

	private async void OnLoadClicked(object? sender, EventArgs e)
	{
		if (FontPicker.SelectedItem is not string selectedFont)
			return;

		var fontFamily = _fonts[selectedFont];
		IconFontFamily = fontFamily;

		var fontData = _fontFiles.TryGetValue(fontFamily, out var fileName)
			? await FontGlyphNameReader.ReadFontDataAsync(fileName)
			: new(new(), new(), 0);

		var glyphs = new List<GlyphItem>();

		// Only include codepoints that actually have glyphs in the font
		foreach (int cp in fontData.ValidCodepoints.Order())
		{
			glyphs.Add(new GlyphItem
			{
				Character = char.ConvertFromUtf32(cp),
				UnicodeCode = $"U+{cp:X4}",
				GlyphName = fontData.Names.GetValueOrDefault(cp, string.Empty),
			});
		}

		GlyphsCollection.ItemsSource = glyphs;
		ShowToast($"{glyphs.Count} ícones");
	}

	private CancellationTokenSource? _toastCts;

	private async void OnGlyphLongPressed(object? sender, TappedEventArgs e)
	{
		if (sender is BindableObject bo && bo.BindingContext is GlyphItem item)
		{
			await Clipboard.Default.SetTextAsync(item.UnicodeCode);
			if (sender is View view)
			{
				await view.ScaleToAsync(0.85, 80);
				await view.ScaleToAsync(1.0, 80);
			}
			ShowToast($"{item.UnicodeCode} copiado!");
		}
	}

	private async void ShowToast(string message)
	{
		_toastCts?.Cancel();
		_toastCts = new CancellationTokenSource();
		var token = _toastCts.Token;

		ToastLabel.Text = message;
		ToastBorder.IsVisible = true;
		ToastBorder.Opacity = 0;
		await ToastBorder.FadeToAsync(1, 200);

		try
		{
			await Task.Delay(1500, token);
		}
		catch (TaskCanceledException) { return; }

		await ToastBorder.FadeToAsync(0, 300);
		ToastBorder.IsVisible = false;
	}

	private void OnFontSizeChanged(object? sender, ValueChangedEventArgs e)
	{
		var size = Math.Round(e.NewValue);
		FontIconSize = size;
		FontSizeLabel.Text = size.ToString("F0");
	}
}

public class GlyphItem
{
	public string Character { get; set; } = string.Empty;
	public string UnicodeCode { get; set; } = string.Empty;
	public string GlyphName { get; set; } = string.Empty;
	public bool HasGlyphName => !string.IsNullOrEmpty(GlyphName);
}
