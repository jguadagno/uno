﻿//-----------------------------------------------------------------------
// The MIT License(MIT)
//
// Original work (https://github.com/AvaloniaUI/Avalonia/blob/4d01dacd77ff17a080b5e5778a18864831e92a63/src/Avalonia.Visuals/Media/PathMarkupParser.cs):
// Copyright (c) 2014 Steven Kirk
// Copyright (c) The Avalonia Project. All rights reserved.
//
// Modified work:
// Copyright (c) 2018 nventive inc. All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Windows.Foundation;
using Windows.UI.Xaml.Media;

namespace Uno.Media
{
    /// <summary>
    /// Parses a path markup string.
    /// </summary>
    public class PathMarkupParser : IDisposable
    {
		private static readonly Dictionary<char, Command> s_commands =
		   new Dictionary<char, Command>
			   {
					{ 'F', Command.FillRule },
					{ 'M', Command.Move },
					{ 'L', Command.Line },
					{ 'H', Command.HorizontalLine },
					{ 'V', Command.VerticalLine },
					{ 'Q', Command.QuadraticBezierCurve },
					{ 'T', Command.SmoothQuadraticBezierCurve },
					{ 'C', Command.CubicBezierCurve },
					{ 'S', Command.SmoothCubicBezierCurve },
					{ 'A', Command.Arc },
					{ 'Z', Command.Close },
			   };

		// Uno specific: Use StreamGeomoetryContext instead of IGeometryContext.
		private StreamGeometryContext _geometryContext;
		private Point _currentPoint;
		private Point? _beginFigurePoint;
		private Point? _previousControlPoint;
		private bool _isOpen;
		private bool _isDisposed;

		/// <summary>
		/// Initializes a new instance of the <see cref="PathMarkupParser"/> class.
		/// </summary>
		/// <param name="geometryContext">The geometry context.</param>
		/// <exception cref="ArgumentNullException">geometryContext</exception>
		public PathMarkupParser(StreamGeometryContext context) // Uno specific: Use StreamGeomoetryContext instead of IGeometryContext.
		{
			if (context == null)
			{
				throw new ArgumentNullException(nameof(context));
			}

			_geometryContext = context;
		}

		private enum Command
		{
			None,
			FillRule,
			Move,
			Line,
			HorizontalLine,
			VerticalLine,
			CubicBezierCurve,
			QuadraticBezierCurve,
			SmoothCubicBezierCurve,
			SmoothQuadraticBezierCurve,
			Arc,
			Close
		}

		void IDisposable.Dispose()
		{
			Dispose(true);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (_isDisposed)
			{
				return;
			}

			if (disposing)
			{
				_geometryContext = null;
			}

			_isDisposed = true;
		}

		private static Point MirrorControlPoint(Point controlPoint, Point center)
		{
			var dir = controlPoint - center;

			return center + -dir;
		}

		/// <summary>
		/// Parses the specified path data and writes the result to the geometryContext of this instance.
		/// </summary>
		/// <param name="s">The path data.</param>
		public void Parse(string s, ref FillRule fillRule) // Uno specific: FillRule parameter.
		{
			var span = s.AsSpan();
			_currentPoint = new Point();

			while (!span.IsEmpty)
			{
				if (!ReadCommand(ref span, out var command, out var relative))
				{
					break;
				}

				bool initialCommand = true;

				do
				{
					if (!initialCommand)
					{
						span = ReadSeparator(span);
					}

					switch (command)
					{
						case Command.None:
							break;
						case Command.FillRule:
							// Uno specific:
							fillRule = GetFillRule(ref span);
							break;
						case Command.Move:
							AddMove(ref span, relative);
							break;
						case Command.Line:
							AddLine(ref span, relative);
							break;
						case Command.HorizontalLine:
							AddHorizontalLine(ref span, relative);
							break;
						case Command.VerticalLine:
							AddVerticalLine(ref span, relative);
							break;
						case Command.CubicBezierCurve:
							AddCubicBezierCurve(ref span, relative);
							break;
						case Command.QuadraticBezierCurve:
							AddQuadraticBezierCurve(ref span, relative);
							break;
						case Command.SmoothCubicBezierCurve:
							AddSmoothCubicBezierCurve(ref span, relative);
							break;
						case Command.SmoothQuadraticBezierCurve:
							AddSmoothQuadraticBezierCurve(ref span, relative);
							break;
						case Command.Arc:
							AddArc(ref span, relative);
							break;
						case Command.Close:
							CloseFigure();
							break;
						default:
							throw new NotSupportedException("Unsupported command");
					}

					initialCommand = false;
				} while (PeekArgument(span));

			}

			if (_isOpen)
			{
				// Uno specific: EndFigure → SetClosedState
				_geometryContext.SetClosedState(false);
			}
		}

		private void CreateFigure()
		{
			if (_isOpen)
			{
				// Uno specific: EndFigure → SetClosedState
				_geometryContext.SetClosedState(false);
			}

			// Uno specific: Extra arguments.
			_geometryContext.BeginFigure(_currentPoint, true, false);

			_beginFigurePoint = _currentPoint;

			_isOpen = true;
		}

		// Uno specific: SetFillRule → GetFillRule
		private FillRule GetFillRule(ref ReadOnlySpan<char> span)
		{
			if (!ReadArgument(ref span, out var fillRule) || fillRule.Length != 1)
			{
				throw new InvalidDataException("Invalid fill rule.");
			}

			FillRule rule;

			switch (fillRule[0])
			{
				case '0':
					rule = FillRule.EvenOdd;
					break;
				case '1':
					// Uno specific: Use Nonzero instead of NonZero as this is how it's named in UWP.
					rule = FillRule.Nonzero;
					break;
				default:
					throw new InvalidDataException("Invalid fill rule");
			}

			return rule;
		}

		private void CloseFigure()
		{
			if (_isOpen)
			{
				// Uno specific: EndFigure → SetClosedState
				_geometryContext.SetClosedState(true);

				if (_beginFigurePoint != null)
				{
					_currentPoint = _beginFigurePoint.Value;
					_beginFigurePoint = null;
				}
			}

			_previousControlPoint = null;

			_isOpen = false;
		}

		private void AddMove(ref ReadOnlySpan<char> span, bool relative)
		{
			var currentPoint = relative
								? ReadRelativePoint(ref span, _currentPoint)
								: ReadPoint(ref span);

			_currentPoint = currentPoint;

			CreateFigure();

			while (PeekArgument(span))
			{
				span = ReadSeparator(span);
				AddLine(ref span, relative);
			}
		}

		private void AddLine(ref ReadOnlySpan<char> span, bool relative)
		{
			_currentPoint = relative
								? ReadRelativePoint(ref span, _currentPoint)
								: ReadPoint(ref span);

			if (!_isOpen)
			{
				CreateFigure();
			}

			// Uno specific: Extra arguments.
			_geometryContext.LineTo(_currentPoint, true, false);
		}

		private void AddHorizontalLine(ref ReadOnlySpan<char> span, bool relative)
		{
			_currentPoint = relative
								? new Point(_currentPoint.X + ReadDouble(ref span), _currentPoint.Y)
								: _currentPoint.WithX(ReadDouble(ref span));

			if (!_isOpen)
			{
				CreateFigure();
			}

			// Uno specific: Extra arguments.
			_geometryContext.LineTo(_currentPoint, true, false);
		}

		private void AddVerticalLine(ref ReadOnlySpan<char> span, bool relative)
		{
			_currentPoint = relative
								? new Point(_currentPoint.X, _currentPoint.Y + ReadDouble(ref span))
								: _currentPoint.WithY(ReadDouble(ref span));

			if (!_isOpen)
			{
				CreateFigure();
			}

			// Uno specific: Extra arguments.
			_geometryContext.LineTo(_currentPoint, true, false);
		}

		private void AddCubicBezierCurve(ref ReadOnlySpan<char> span, bool relative)
		{
			var point1 = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			span = ReadSeparator(span);

			var point2 = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			_previousControlPoint = point2;

			span = ReadSeparator(span);

			var point3 = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			if (!_isOpen)
			{
				CreateFigure();
			}

			// Uno specific: CubicBezierTo → BezierTo
			_geometryContext.BezierTo(point1, point2, point3, true, false); // Uno specific: Extra arguments.

			_currentPoint = point3;
		}

		private void AddQuadraticBezierCurve(ref ReadOnlySpan<char> span, bool relative)
		{
			var start = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			_previousControlPoint = start;

			span = ReadSeparator(span);

			var end = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			if (!_isOpen)
			{
				CreateFigure();
			}

			_geometryContext.QuadraticBezierTo(start, end, true, false); // Uno specific: Extra arguments.

			_currentPoint = end;
		}

		private void AddSmoothCubicBezierCurve(ref ReadOnlySpan<char> span, bool relative)
		{
			var point2 = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			span = ReadSeparator(span);

			var end = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			if (_previousControlPoint != null)
			{
				_previousControlPoint = MirrorControlPoint((Point)_previousControlPoint, _currentPoint);
			}

			if (!_isOpen)
			{
				CreateFigure();
			}

			// Uno specific: CubicBezierTo → BezierTo
			_geometryContext.BezierTo(_previousControlPoint ?? _currentPoint, point2, end, true, false); // Uno specific: Extra arguments.

			_previousControlPoint = point2;

			_currentPoint = end;
		}

		private void AddSmoothQuadraticBezierCurve(ref ReadOnlySpan<char> span, bool relative)
		{
			var end = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			if (_previousControlPoint != null)
			{
				_previousControlPoint = MirrorControlPoint((Point)_previousControlPoint, _currentPoint);
			}

			if (!_isOpen)
			{
				CreateFigure();
			}

			_geometryContext.QuadraticBezierTo(_previousControlPoint ?? _currentPoint, end, true, false); // Uno specific: Extra arguments.

			_currentPoint = end;
		}

		private void AddArc(ref ReadOnlySpan<char> span, bool relative)
		{
			var size = ReadSize(ref span);

			span = ReadSeparator(span);

			var rotationAngle = ReadDouble(ref span);
			span = ReadSeparator(span);
			var isLargeArc = ReadBool(ref span);

			span = ReadSeparator(span);

			// Uno specific: CounterClockwise → Counterclockwise
			var sweepDirection = ReadBool(ref span) ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;

			span = ReadSeparator(span);

			var end = relative
					? ReadRelativePoint(ref span, _currentPoint)
					: ReadPoint(ref span);

			if (!_isOpen)
			{
				CreateFigure();
			}

			_geometryContext.ArcTo(end, size, rotationAngle, isLargeArc, sweepDirection, true, false); // Uno specific: Extra arguments.

			_currentPoint = end;

			_previousControlPoint = null;
		}

		private static bool PeekArgument(ReadOnlySpan<char> span)
		{
			span = SkipWhitespace(span);

			return !span.IsEmpty && (span[0] == ',' || span[0] == '-' || span[0] == '.' || char.IsDigit(span[0]));
		}

		private static bool ReadArgument(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> argument)
		{
			remaining = SkipWhitespace(remaining);
			if (remaining.IsEmpty)
			{
				argument = ReadOnlySpan<char>.Empty;
				return false;
			}

			var valid = false;
			int i = 0;
			if (remaining[i] == '-')
			{
				i++;
			}
			for (; i < remaining.Length && char.IsNumber(remaining[i]); i++) valid = true;

			if (i < remaining.Length && remaining[i] == '.')
			{
				valid = false;
				i++;
			}
			for (; i < remaining.Length && char.IsNumber(remaining[i]); i++) valid = true;

			if (i < remaining.Length)
			{
				// scientific notation
				if (remaining[i] == 'E' || remaining[i] == 'e')
				{
					valid = false;
					i++;
					if (remaining[i] == '-' || remaining[i] == '+')
					{
						i++;
						for (; i < remaining.Length && char.IsNumber(remaining[i]); i++) valid = true;
					}
				}
			}

			if (!valid)
			{
				argument = ReadOnlySpan<char>.Empty;
				return false;
			}
			argument = remaining.Slice(0, i);
			remaining = remaining.Slice(i);
			return true;
		}


		private static ReadOnlySpan<char> ReadSeparator(ReadOnlySpan<char> span)
		{
			span = SkipWhitespace(span);
			if (!span.IsEmpty && span[0] == ',')
			{
				span = span.Slice(1);
			}
			return span;
		}

		private static ReadOnlySpan<char> SkipWhitespace(ReadOnlySpan<char> span)
		{
			int i = 0;
			for (; i < span.Length && char.IsWhiteSpace(span[i]); i++) ;
			return span.Slice(i);
		}

		// Uno docs: Implementation (currently) different than Avalonia due to:
		// https://github.com/unoplatform/uno/issues/2855
		private bool ReadBool(ref ReadOnlySpan<char> span)
		{
			span = SkipWhitespace(span);
			if (span.IsEmpty)
			{
				throw new InvalidDataException("Cannot read bool from empty span.");
			}

			var c = span[0];
			span = span.Slice(1);

			switch (c)
			{
				case '0':
					return false;
				case '1':
					return true;
				default:
					throw new InvalidDataException("Invalid bool rule");
			}
		}

		private double ReadDouble(ref ReadOnlySpan<char> span)
		{
			if (!ReadArgument(ref span, out var doubleValue))
			{
				throw new InvalidDataException("Invalid double value");
			}

			return double.Parse(doubleValue.ToString(), CultureInfo.InvariantCulture);
		}

		private Size ReadSize(ref ReadOnlySpan<char> span)
		{
			var width = ReadDouble(ref span);
			span = ReadSeparator(span);
			var height = ReadDouble(ref span);
			return new Size(width, height);
		}

		private Point ReadPoint(ref ReadOnlySpan<char> span)
		{
			var x = ReadDouble(ref span);
			span = ReadSeparator(span);
			var y = ReadDouble(ref span);
			return new Point(x, y);
		}

		private Point ReadRelativePoint(ref ReadOnlySpan<char> span, Point origin)
		{
			var x = ReadDouble(ref span);
			span = ReadSeparator(span);
			var y = ReadDouble(ref span);
			return new Point(origin.X + x, origin.Y + y);
		}

		private bool ReadCommand(ref ReadOnlySpan<char> span, out Command command, out bool relative)
		{
			span = SkipWhitespace(span);
			if (span.IsEmpty)
			{
				command = default;
				relative = false;
				return false;
			}
			var c = span[0];
			if (!s_commands.TryGetValue(char.ToUpperInvariant(c), out command))
			{
				throw new InvalidDataException("Unexpected path command '" + c + "'.");
			}
			relative = char.IsLower(c);
			span = span.Slice(1);
			return true;
		}
	}
}
