using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Uno.Foundation;
using Windows.Foundation;
using Windows.UI.Xaml.Input;
using Windows.System;
using Uno.UI;
using Uno.UI.Xaml;

#if HAS_UNO_WINUI
using Microsoft.UI.Input;
#else
using Windows.Devices.Input;
using Windows.UI.Input;
#endif

namespace Windows.UI.Xaml
{
	public partial class UIElement : DependencyObject
	{
		// Ref:
		// https://www.w3.org/TR/pointerevents/
		// https://developer.mozilla.org/en-US/docs/Web/API/PointerEvent
		// https://developer.mozilla.org/en-US/docs/Web/API/WheelEvent

#region Native event registration handling
		partial void OnGestureRecognizerInitialized(GestureRecognizer recognizer)
		{
			// When a gesture recognizer is initialized, we subscribe to pointer events in order to feed it.
			// Note: We register to Move, so it will also register Enter, Exited, Pressed, Released and Cancel.
			//		 Gesture recognizer does not needs CaptureLost nor Wheel events.
			AddPointerHandler(PointerMovedEvent, 1, default, default);
		}

		partial void AddPointerHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo)
		{
			if (handlersCount != 1 || _registeredRoutedEvents.HasFlag(routedEvent.Flag))
			{
				return;
			}

			// In order to ensure valid pressed and over state, we ** must ** subscribe to all the related events
			// before subscribing to other pointer events.
			if (!_registeredRoutedEvents.HasFlag(RoutedEventFlag.PointerEntered))
			{
				WindowManagerInterop.RegisterPointerEventsOnView(HtmlId);

				_registeredRoutedEvents |=
					  RoutedEventFlag.PointerEntered
					| RoutedEventFlag.PointerExited
					| RoutedEventFlag.PointerPressed
					| RoutedEventFlag.PointerReleased
					| RoutedEventFlag.PointerCanceled;

				// Note: we use 'pointerenter' and 'pointerleave' which are not bubbling natively
				//		 as on UWP, even if the event are RoutedEvents, PointerEntered and PointerExited
				//		 are routed only in some particular cases (entering at once on multiple controls),
				//		 it's easier to handle this in managed code.
				RegisterEventHandler("pointerenter", (RawEventHandler)DispatchNativePointerEnter, GenericEventHandlers.RaiseRawEventHandler);
				RegisterEventHandler("pointerleave", (RawEventHandler)DispatchNativePointerLeave, GenericEventHandlers.RaiseRawEventHandler);
				RegisterEventHandler("pointerdown", (RawEventHandler)DispatchNativePointerDown, GenericEventHandlers.RaiseRawEventHandler);
				RegisterEventHandler("pointerup", (RawEventHandler)DispatchNativePointerUp, GenericEventHandlers.RaiseRawEventHandler);
				RegisterEventHandler("pointercancel", (RawEventHandler)DispatchNativePointerCancel, GenericEventHandlers.RaiseRawEventHandler); //https://www.w3.org/TR/pointerevents/#the-pointercancel-event
			}

			switch (routedEvent.Flag)
			{
				case RoutedEventFlag.PointerEntered:
				case RoutedEventFlag.PointerExited:
				case RoutedEventFlag.PointerPressed:
				case RoutedEventFlag.PointerReleased:
				case RoutedEventFlag.PointerCanceled:
					// All event above are automatically subscribed
					break;

				case RoutedEventFlag.PointerMoved:
					_registeredRoutedEvents |= RoutedEventFlag.PointerMoved;
					RegisterEventHandler(
						"pointermove",
						handler: (RawEventHandler)DispatchNativePointerMove,
						invoker: GenericEventHandlers.RaiseRawEventHandler,
						onCapturePhase: false,
						eventExtractor: HtmlEventExtractor.PointerEventExtractor
					);
					break;

				case RoutedEventFlag.PointerCaptureLost:
					// No native registration: Captures are handled in managed code only
					_registeredRoutedEvents |= routedEvent.Flag;
					break;

				case RoutedEventFlag.PointerWheelChanged:
					_registeredRoutedEvents |= RoutedEventFlag.PointerMoved;
					RegisterEventHandler(
						"wheel",
						handler: (RawEventHandler)DispatchNativePointerWheel,
						invoker: GenericEventHandlers.RaiseRawEventHandler,
						onCapturePhase: false,
						eventExtractor: HtmlEventExtractor.PointerEventExtractor
					);
					break;

				default:
					Application.Current.RaiseRecoverableUnhandledException(new NotImplementedException($"Pointer event {routedEvent.Name} is not supported on this platform"));
					break;
			}
		}
#endregion

#region Native event dispatch
		// Note for Enter and Leave:
		//	canBubble: true is actually not true.
		//	When we subscribe to pointer enter in a window, we don't receive pointer enter for each sub-views!
		//	But the web-browser will actually behave like WinUI for pointerenter and pointerleave, so here by setting it to true,
		//	we just ensure that the managed code won't try to bubble it by its own.
		//	However, if the event is Handled in managed, it will then bubble while it should not! https://github.com/unoplatform/uno/issues/3007
		// Note about the HtmlEventDispatchResult:
		//	For pointer events we never want to prevent the default behavior.
		//	Especially for wheel where preventing the default would break scrolling.
		//	cf. remarks on HtmlEventDispatchResult.PreventDefault
		private static HtmlEventDispatchResult DispatchNativePointerEnter(UIElement target, string eventPayload)
			=> TryParse(eventPayload, out var args) && target.OnNativePointerEnter(ToPointerArgs(target, args))
				? HtmlEventDispatchResult.StopPropagation
				: HtmlEventDispatchResult.Ok;

		private static HtmlEventDispatchResult DispatchNativePointerLeave(UIElement target, string eventPayload)
			=> TryParse(eventPayload, out var args) && target.OnNativePointerExited(ToPointerArgs(target, args))
				? HtmlEventDispatchResult.StopPropagation
				: HtmlEventDispatchResult.Ok;

		private static HtmlEventDispatchResult DispatchNativePointerDown(UIElement target, string eventPayload)
			=> TryParse(eventPayload, out var args) && target.OnNativePointerDown(ToPointerArgs(target, args, isInContact: true))
				? HtmlEventDispatchResult.StopPropagation
				: HtmlEventDispatchResult.Ok;

		private static HtmlEventDispatchResult DispatchNativePointerUp(UIElement target, string eventPayload)
			=> TryParse(eventPayload, out var args) && target.OnNativePointerUp(ToPointerArgs(target, args, isInContact: false))
				? HtmlEventDispatchResult.StopPropagation
				: HtmlEventDispatchResult.Ok;

		private static HtmlEventDispatchResult DispatchNativePointerMove(UIElement target, string eventPayload)
			=> TryParse(eventPayload, out var args) && target.OnNativePointerMove(ToPointerArgs(target, args))
				? HtmlEventDispatchResult.StopPropagation
				: HtmlEventDispatchResult.Ok;

		private static HtmlEventDispatchResult DispatchNativePointerCancel(UIElement target, string eventPayload)
			=> TryParse(eventPayload, out var args) && target.OnNativePointerCancel(ToPointerArgs(target, args, isInContact: false), isSwallowedBySystem: true)
				? HtmlEventDispatchResult.StopPropagation
				: HtmlEventDispatchResult.Ok;

		private static HtmlEventDispatchResult DispatchNativePointerWheel(UIElement target, string eventPayload)
		{
			if (TryParse(eventPayload, out var args))
			{
				// We might have a scroll along 2 directions at once (touch pad).
				// As WinUI does support scrolling only along one direction at a time, we have to raise 2 managed events.

				var handled = false;
				if (args.wheelDeltaX != 0)
				{
					handled |= target.OnNativePointerWheel(ToPointerArgs(target, args, wheel: (true, args.wheelDeltaX), isInContact: null /* maybe */));
				}
				if (args.wheelDeltaY != 0)
				{
					// Note: Web browser vertical scrolling is the opposite compared to WinUI!
					handled |= target.OnNativePointerWheel(ToPointerArgs(target, args, wheel: (false, -args.wheelDeltaY), isInContact: null /* maybe */));
				}
				return handled
					? HtmlEventDispatchResult.StopPropagation
					: HtmlEventDispatchResult.Ok;
			}
			else
			{
				return HtmlEventDispatchResult.Ok;
			}
		}

		private static bool TryParse(string eventPayload, out NativePointerEventArgs args)
		{
			var parts = eventPayload?.Split(';');
			if (parts?.Length != 13)
			{
				args = default;
				return false;
			}

			args = new NativePointerEventArgs { 
				pointerId = double.Parse(parts[0], CultureInfo.InvariantCulture), // On Safari for iOS, the ID might be negative!
				x = double.Parse(parts[1], CultureInfo.InvariantCulture),
				y = double.Parse(parts[2], CultureInfo.InvariantCulture),
				ctrl = parts[3] == "1",
				shift = parts[4] == "1",
				buttons = int.Parse(parts[5], CultureInfo.InvariantCulture),
				buttonUpdate = int.Parse(parts[6], CultureInfo.InvariantCulture),
				typeStr = parts[7],
				srcHandle = int.Parse(parts[8], CultureInfo.InvariantCulture),
				timestamp = double.Parse(parts[9], CultureInfo.InvariantCulture),
				pressure = double.Parse(parts[10], CultureInfo.InvariantCulture),
				wheelDeltaX = double.Parse(parts[11], CultureInfo.InvariantCulture),
				wheelDeltaY = double.Parse(parts[12], CultureInfo.InvariantCulture),
			};
			return true;
		}

		private static PointerRoutedEventArgs ToPointerArgs(
			UIElement snd,
			NativePointerEventArgs args,
			bool? isInContact = null,
			(bool isHorizontalWheel, double delta) wheel = default)
		{
			var pointerId = (uint)args.pointerId;
			var src = GetElementFromHandle(args.srcHandle) ?? (UIElement)snd;
			var position = new Point(args.x, args.y);
			var pointerType = ConvertPointerTypeString(args.typeStr);
			var keyModifiers = VirtualKeyModifiers.None;
			if (args.ctrl) keyModifiers |= VirtualKeyModifiers.Control;
			if (args.shift) keyModifiers |= VirtualKeyModifiers.Shift;

			return new PointerRoutedEventArgs(
				args.timestamp,
				pointerId,
				pointerType,
				position,
				isInContact ?? ((UIElement)snd).IsPressed(pointerId),
				(WindowManagerInterop.HtmlPointerButtonsState)args.buttons,
				(WindowManagerInterop.HtmlPointerButtonUpdate)args.buttonUpdate,
				keyModifiers,
				args.pressure,
				wheel,
				src);
		}

		private static PointerDeviceType ConvertPointerTypeString(string typeStr)
		{
			PointerDeviceType type;
			switch (typeStr.ToUpper())
			{
				case "MOUSE":
				default:
					type = PointerDeviceType.Mouse;
					break;
				// Note: As of 2019-11-28, once pen pressed events pressed/move/released are reported as TOUCH on Firefox
				//		 https://bugzilla.mozilla.org/show_bug.cgi?id=1449660
				case "PEN":
					type = PointerDeviceType.Pen;
					break;
				case "TOUCH":
					type = PointerDeviceType.Touch;
					break;
			}

			return type;
		}
#endregion

#region Capture
		partial void OnManipulationModeChanged(ManipulationModes _, ManipulationModes newMode)
			=> SetStyle("touch-action", newMode == ManipulationModes.None ? "none" : "auto");

		partial void CapturePointerNative(Pointer pointer)
		{
			var command = "Uno.UI.WindowManager.current.setPointerCapture(" + HtmlId + ", " + pointer.PointerId + ");";
			WebAssemblyRuntime.InvokeJS(command);

			if (pointer.PointerDeviceType != PointerDeviceType.Mouse)
			{
				SetStyle("touch-action", "none");
			}
		}

		partial void ReleasePointerNative(Pointer pointer)
		{
			var command = "Uno.UI.WindowManager.current.releasePointerCapture(" + HtmlId + ", " + pointer.PointerId + ");";
			WebAssemblyRuntime.InvokeJS(command);

			if (pointer.PointerDeviceType != PointerDeviceType.Mouse && ManipulationMode != ManipulationModes.None)
			{
				SetStyle("touch-action", "auto");
			}
		}
#endregion

#region HitTestVisibility
		internal void UpdateHitTest()
		{
			this.CoerceValue(HitTestVisibilityProperty);
		}

		[GeneratedDependencyProperty(DefaultValue = HitTestability.Collapsed, ChangedCallback = true, CoerceCallback = true, Options = FrameworkPropertyMetadataOptions.Inherits)]
		internal static DependencyProperty HitTestVisibilityProperty { get; } = CreateHitTestVisibilityProperty();

		internal HitTestability HitTestVisibility
		{
			get => GetHitTestVisibilityValue();
			set => SetHitTestVisibilityValue(value);
		}

		/// <summary>
		/// This calculates the final hit-test visibility of an element.
		/// </summary>
		/// <returns></returns>
		private object CoerceHitTestVisibility(object baseValue)
		{
			// The HitTestVisibilityProperty is never set directly. This means that baseValue is always the result of the parent's CoerceHitTestVisibility.
			var baseHitTestVisibility = (HitTestability)baseValue;

			// If the parent is collapsed, we should be collapsed as well. This takes priority over everything else, even if we would be visible otherwise.
			if (baseHitTestVisibility == HitTestability.Collapsed)
			{
				return HitTestability.Collapsed;
			}

			// If we're not locally hit-test visible, visible, or enabled, we should be collapsed. Our children will be collapsed as well.
			// SvgElements are an exception here since they won't be loaded.
			if (!(IsLoaded || HtmlTagIsSvg) || !IsHitTestVisible || Visibility != Visibility.Visible || !IsEnabledOverride())
			{
				return HitTestability.Collapsed;
			}

			// If we're not hit (usually means we don't have a Background/Fill), we're invisible. Our children will be visible or not, depending on their state.
			if (!IsViewHit())
			{
				return HitTestability.Invisible;
			}

			// If we're not collapsed or invisible, we can be targeted by hit-testing. This means that we can be the source of pointer events.
			return HitTestability.Visible;
		}

		private protected virtual void OnHitTestVisibilityChanged(HitTestability oldValue, HitTestability newValue)
		{
			ApplyHitTestVisibility(newValue);
		}

		private void ApplyHitTestVisibility(HitTestability value)
		{
			if (value == HitTestability.Visible)
			{
				// By default, elements have 'pointer-event' set to 'auto' (see Uno.UI.css .uno-uielement class).
				// This means that they can be the target of hit-testing and will raise pointer events when interacted with.
				// This is aligned with HitTestVisibilityProperty's default value of Visible.
				WindowManagerInterop.SetPointerEvents(HtmlId, true);
			}
			else
			{
				// If HitTestVisibilityProperty is calculated to Invisible or Collapsed,
				// we don't want to be the target of hit-testing and raise any pointer events.
				// This is done by setting 'pointer-events' to 'none'.
				WindowManagerInterop.SetPointerEvents(HtmlId, false);
			}

			if (FeatureConfiguration.UIElement.AssignDOMXamlProperties)
			{
				UpdateDOMProperties();
			}
		}

		internal void SetHitTestVisibilityForRoot()
		{
			// Root element must be visible to hit testing, regardless of the other properties values.
			// The default value of HitTestVisibility is collapsed to avoid spending time coercing to a
			// Collapsed.
			HitTestVisibility = HitTestability.Visible;
		}

		internal void ClearHitTestVisibilityForRoot()
		{
			this.ClearValue(HitTestVisibilityProperty);
		}

#endregion

		// TODO: This should be marshaled instead of being parsed! https://github.com/unoplatform/uno/issues/2116
		private struct NativePointerEventArgs
		{
			public double pointerId; // Warning: This is a Number in JS, and it might be negative on safari for iOS
			public double x;
			public double y;
			public bool ctrl;
			public bool shift;
			public int buttons;
			public int buttonUpdate;
			public string typeStr;
			public int srcHandle;
			public double timestamp;
			public double pressure;
			public double wheelDeltaX;
			public double wheelDeltaY;
		}
	}
}
