﻿#nullable enable

using System;
using System.Linq;
using Gtk;
using Uno.UI.Xaml.Controls.Extensions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using GLib;
using Pango;
using Uno.Disposables;
using Uno.UI.Runtime.Skia.GTK.UI.Text;
using GtkWindow = Gtk.Window;
using Object = GLib.Object;
using Point = Windows.Foundation.Point;
using Scale = Pango.Scale;
using System.Diagnostics;
using Windows.UI.Xaml.Media;

namespace Uno.UI.Runtime.Skia.GTK.Extensions.UI.Xaml.Controls
{
	internal class TextBoxViewExtension : ITextBoxViewExtension
	{
		private const string TextBoxViewCssClass = "textboxview";

		private readonly TextBoxView _owner;
		private readonly GtkWindow _window;
		private ContentControl? _contentElement;
		private Widget? _currentInputWidget;
		private bool _handlingTextChanged;

		private readonly SerialDisposable _textChangedDisposable = new SerialDisposable();
		private readonly SerialDisposable _textBoxEventSubscriptions = new SerialDisposable();

		public TextBoxViewExtension(TextBoxView owner, GtkWindow window)
		{
			_owner = owner ?? throw new ArgumentNullException(nameof(owner));
			_window = window ?? throw new ArgumentNullException(nameof(window));
		}

		private Fixed GetWindowTextInputLayer()
		{
			// now we have the GtkEventBox
			var overlay = (Overlay)((EventBox)_window.Child).Child;
			return overlay.Children.OfType<Fixed>().First();
		}

		public void StartEntry()
		{
			var textBox = _owner.TextBox;
			if (textBox == null)
			{
				// The parent TextBox must exist as source of properties.
				return;
			}

			_contentElement = textBox.ContentElement;
			EnsureWidget(textBox);
			var textInputLayer = GetWindowTextInputLayer();
			textInputLayer.Put(_currentInputWidget!, 0, 0);

			textBox.SizeChanged += ContentElementSizeChanged;
			textBox.LayoutUpdated += ContentElementLayoutUpdated;
			_textBoxEventSubscriptions.Disposable = Disposable.Create(() =>
			{
				textBox.SizeChanged -= ContentElementSizeChanged;
				textBox.LayoutUpdated -= ContentElementLayoutUpdated;
			});

			UpdateNativeView();
			SetWidgetText(textBox.Text);

			UpdateSize();
			UpdatePosition();

			textInputLayer.ShowAll();
			_currentInputWidget!.HasFocus = true;
		}

		public void EndEntry()
		{
			if (GetInputText() is { } inputText)
			{
				_owner.UpdateTextFromNative(inputText);
			}

			_contentElement = null;
			_textBoxEventSubscriptions.Disposable = null;

			if (_currentInputWidget != null)
			{
				var textInputLayer = GetWindowTextInputLayer();
				textInputLayer.Remove(_currentInputWidget);
			}
		}

		public void UpdateNativeView()
		{
			if (_currentInputWidget == null)
			{
				// If the input widget does not exist, we don't need to update it.
				return;
			}

			var textBox = _owner.TextBox;
			if (textBox == null)
			{
				// The parent TextBox must exist as source of properties.
				return;
			}

			EnsureWidget(textBox);

			var fontDescription = new FontDescription
			{
				Weight = textBox.FontWeight.ToPangoWeight(),
				AbsoluteSize = textBox.FontSize * Scale.PangoScale,
			};
#pragma warning disable CS0612 // Type or member is obsolete
			_currentInputWidget.OverrideFont(fontDescription);
#pragma warning restore CS0612 // Type or member is obsolete

			switch (_currentInputWidget)
			{
				case Entry entry:
					UpdateEntryProperties(entry, textBox);
					break;
				case TextView textView:
					UpdateTextViewProperties(textView, textBox);
					break;
			}
		}

		public void UpdateSize()
		{
			if (_contentElement == null || _currentInputWidget == null)
			{
				return;
			}

			var textInputLayer = GetWindowTextInputLayer();
			if (textInputLayer.Children.Contains(_currentInputWidget))
			{
				_currentInputWidget?.SetSizeRequest((int)_contentElement.ActualWidth, (int)_contentElement.ActualHeight);
			}
		}

		public void UpdatePosition()
		{
			if (_contentElement == null || _currentInputWidget == null)
			{
				return;
			}

			var transformToRoot = _contentElement.TransformToVisual(Windows.UI.Xaml.Window.Current.Content);
			var point = transformToRoot.TransformPoint(new Point(0, 0));
			var textInputLayer = GetWindowTextInputLayer();
			if (textInputLayer.Children.Contains(_currentInputWidget))
			{
				textInputLayer.Move(_currentInputWidget, (int)point.X, (int)point.Y);
			}
		}

		public void SetTextNative(string text) => SetWidgetText(text);

		private void UpdateTextViewProperties(TextView textView, TextBox textBox)
		{
			textView.Editable = !textBox.IsReadOnly;
		}

		private void UpdateEntryProperties(Entry entry, TextBox textBox)
		{
			entry.IsEditable = !textBox.IsReadOnly;
			entry.MaxLength = textBox.MaxLength;
		}

		private void EnsureWidget(TextBox textBox)
		{
			var isPassword = false;
			var isPasswordVisible = true;
			if (textBox is PasswordBox passwordBox)
			{
				isPassword = true;
				isPasswordVisible = passwordBox.PasswordRevealMode == PasswordRevealMode.Visible;
			}

			// On UWP, A PasswordBox doesn't have AcceptsReturn property.
			// The property exists on Uno because PasswordBox incorrectly inherits TextBox.
			// If we have PasswordBox, ignore AcceptsReturnValue and always use Gtk.Entry
			var acceptsReturn = textBox.AcceptsReturn && !isPassword;

			var isIncompatibleInputType =
				(acceptsReturn && !(_currentInputWidget is TextView)) ||
				(!acceptsReturn && !(_currentInputWidget is Entry));
			if (isIncompatibleInputType)
			{
				var inputText = GetInputText();
				_currentInputWidget = CreateInputWidget(acceptsReturn, isPassword, isPasswordVisible);
				SetWidgetText(inputText ?? string.Empty);
			}
		}

		private Widget CreateInputWidget(bool acceptsReturn, bool isPassword, bool isPasswordVisible)
		{
			Debug.Assert(!acceptsReturn || !isPassword);
			Widget widget;
			if (acceptsReturn)
			{
				var textView = new TextView();
				textView.Buffer.Changed += WidgetTextChanged;
				_textChangedDisposable.Disposable = Disposable.Create(() => textView.Buffer.Changed -= WidgetTextChanged);
				widget = textView;
			}
			else
			{
				var entry = new Entry();
				if (isPassword)
				{
					entry.InputPurpose = InputPurpose.Password;
					entry.Visibility = isPasswordVisible;
				}

				entry.Changed += WidgetTextChanged;
				_textChangedDisposable.Disposable = Disposable.Create(() => entry.Changed -= WidgetTextChanged);
				widget = entry;
			}
			widget.StyleContext.AddClass(TextBoxViewCssClass);
			return widget;
		}

		private void WidgetTextChanged(object? sender, EventArgs e)
		{
			// Avoid stack overflow as updating text from
			// shared code briefly sets empty string and causes
			// infinite loop
			if (_handlingTextChanged)
			{
				return;
			}

			try
			{
				_handlingTextChanged = true;
				_owner.UpdateTextFromNative(GetInputText() ?? string.Empty);

			}
			finally
			{
				_handlingTextChanged = false;
			}
		}

		private string? GetInputText() =>
			_currentInputWidget switch
			{
				Entry entry => entry.Text,
				TextView textView => textView.Buffer.Text,
				_ => null
			};

		private void SetWidgetText(string text)
		{
			switch (_currentInputWidget)
			{
				case Entry entry:
					entry.Text = text;
					break;
				case TextView textView:
					textView.Buffer.Text = text;
					break;
			};
		}

		private void ContentElementLayoutUpdated(object? sender, object e)
		{
			UpdateSize();
			UpdatePosition();
		}

		private void ContentElementSizeChanged(object sender, Windows.UI.Xaml.SizeChangedEventArgs args)
		{
			UpdateSize();
			UpdatePosition();
		}

		public void SetIsPassword(bool isPassword)
		{
			if (_currentInputWidget is Entry entry)
			{
				entry.Visibility = !isPassword;
			}
		}

		public void Select(int start, int length)
		{
			var textBox = _owner.TextBox;
			if (textBox == null)
			{
				return;
			}

			EnsureWidget(textBox);
			if (_currentInputWidget is Entry entry)
			{
				textBox.UpdateFocusState(FocusState.Programmatic);
				entry.SelectRegion(start_pos: start, end_pos: start + length);
			}
			// TODO: Handle TextView..
		}

		public int GetSelectionStart()
		{
			if (_currentInputWidget is Entry entry)
			{
				entry.GetSelectionBounds(out var start, out _);
				return start;
			}
			else if (_currentInputWidget is TextView textView)
			{
				textView.Buffer.GetSelectionBounds(out var start, out _);
				return start.Offset; // TODO: Confirm this implementation is correct.
			}

			return 0;
		}

		public int GetSelectionLength()
		{
			if (_currentInputWidget is Entry entry)
			{
				entry.GetSelectionBounds(out var start, out var end);
				return end - start;
			}
			else if (_currentInputWidget is TextView textView)
			{
				textView.Buffer.GetSelectionBounds(out var start, out var end);
				return end.Offset - start.Offset;
			}

			return 0;
		}

		public void SetForeground(Windows.UI.Xaml.Media.Brush brush)
		{
			if (brush is SolidColorBrush scb)
			{
				_currentInputWidget?.OverrideColor(StateFlags.Normal, new Gdk.RGBA
				{
					Red = scb.ColorWithOpacity.R,
					Green = scb.ColorWithOpacity.G,
					Blue = scb.ColorWithOpacity.B,
					Alpha = scb.ColorWithOpacity.A
				});
			}
		}
	}
}
