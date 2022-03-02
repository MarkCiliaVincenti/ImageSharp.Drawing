// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace SixLabors.ImageSharp.Drawing
{
    /// <summary>
    /// A aggregate of <see cref="ILineSegment"/>s making a single logical path.
    /// </summary>
    /// <seealso cref="IPath" />
    public class Path : IPath, ISimplePath, IPathInternals, IInternalPathOwner
    {
        private readonly ILineSegment[] lineSegments;
        private InternalPath innerPath;

        /// <summary>
        /// Initializes a new instance of the <see cref="Path"/> class.
        /// </summary>
        /// <param name="segments">The segments.</param>
        public Path(IEnumerable<ILineSegment> segments)
            : this(segments?.ToArray())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Path" /> class.
        /// </summary>
        /// <param name="path">The path.</param>
        public Path(Path path)
            : this(path.LineSegments)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Path"/> class.
        /// </summary>
        /// <param name="segments">The segments.</param>
        public Path(params ILineSegment[] segments)
            => this.lineSegments = segments ?? throw new ArgumentNullException(nameof(segments));

        /// <inheritdoc/>
        bool ISimplePath.IsClosed => this.IsClosed;

        /// <inheritdoc cref="ISimplePath.IsClosed"/>
        public virtual bool IsClosed => false;

        /// <inheritdoc/>
        public ReadOnlyMemory<PointF> Points => this.InnerPath.Points();

        /// <inheritdoc />
        public RectangleF Bounds => this.InnerPath.Bounds;

        /// <inheritdoc />
        public PathTypes PathType => this.IsClosed ? PathTypes.Open : PathTypes.Closed;

        /// <summary>
        /// Gets the maximum number intersections that a shape can have when testing a line.
        /// </summary>
        internal int MaxIntersections => this.InnerPath.PointCount;

        /// <summary>
        /// Gets readonly collection of line segments.
        /// </summary>
        public IReadOnlyList<ILineSegment> LineSegments => this.lineSegments;

        /// <summary>
        /// Gets or sets a value indicating whether close or collinear vertices should be removed. TEST ONLY!
        /// </summary>
        internal bool RemoveCloseAndCollinearPoints { get; set; } = true;

        private InternalPath InnerPath =>
            this.innerPath ??= new InternalPath(this.lineSegments, this.IsClosed, this.RemoveCloseAndCollinearPoints);

        /// <inheritdoc />
        public virtual IPath Transform(Matrix3x2 matrix)
        {
            if (matrix.IsIdentity)
            {
                return this;
            }

            var segments = new ILineSegment[this.lineSegments.Length];

            for (int i = 0; i < this.LineSegments.Count; i++)
            {
                segments[i] = this.lineSegments[i].Transform(matrix);
            }

            return new Path(segments);
        }

        /// <inheritdoc />
        public IPath AsClosedPath()
        {
            if (this.IsClosed)
            {
                return this;
            }

            return new Polygon(this.LineSegments);
        }

        /// <inheritdoc />
        public IEnumerable<ISimplePath> Flatten()
        {
            yield return this;
        }

        /// <inheritdoc/>
        SegmentInfo IPathInternals.PointAlongPath(float distance)
           => this.InnerPath.PointAlongPath(distance);

        /// <inheritdoc/>
        IReadOnlyList<InternalPath> IInternalPathOwner.GetRingsAsInternalPath() => new[] { this.InnerPath };

        public static bool TryParseSvgPath(ReadOnlySpan<char> data, out IPath value)
        {
            value = null;

            var builder = new PathBuilder();

            //parse svg
            PointF first = PointF.Empty;
            PointF c = PointF.Empty;
            PointF lastc = PointF.Empty;
            // stackalloc ???
            var points = new PointF[3].AsSpan();

            char op = '\0';
            char previousOp = '\0';
            bool relative = false;
            while (true)
            {
                data = data.TrimStart();
                if (data.Length == 0)
                {
                    break;
                }

                char ch = data[0];
                if (char.IsDigit(ch) || ch == '-' || ch == '+' || ch == '.')
                {
                    // are we are the end of the string or we are at the end of the path
                    if (data.Length == 0 || op == 'Z')
                    {
                        return false;
                    }
                }
                else if (IsSeperator(ch))
                {
                    data = TrimSeperator(data);
                }
                else
                {
                    op = ch;
                    relative = false;
                    if (char.IsLower(op))
                    {
                        op = char.ToUpper(op);
                        relative = true;
                    }

                    data = TrimSeperator(data.Slice(1));
                }
                switch (op)
                {
                    case 'M':
                        data = FindPoints(data, points, 1, relative, c);
                        builder.MoveTo(points[0]);
                        previousOp = '\0';
                        op = 'L';
                        c = points[0];
                        break;
                    case 'L':
                        data = FindPoints(data, points, 1, relative, c);
                        builder.LineTo(points[0]);
                        c = points[0];
                        break;
                    case 'H':
                    {
                        data = FindScaler(data, out float x);
                        if (relative)
                        {
                            x += c.X;
                        }

                        builder.LineTo(x, c.Y);
                        c.X = x;
                    }

                    break;
                    case 'V':
                    {
                        data = FindScaler(data, out float y);
                        if (relative)
                        {
                            y += c.Y;
                        }

                        builder.LineTo(c.X, y);
                        c.Y = y;
                    }
                    break;
                    case 'C':
                        data = FindPoints(data, points, 3, relative, c);
                        builder.CubicBezierTo(points[0], points[1], points[2]);
                        lastc = points[1];
                        c = points[2];
                        break;
                    case 'S':
                        data = FindPoints(data, points, 2, relative, c);
                        points[0] = c;
                        if (previousOp == 'C' || previousOp == 'S')
                        {
                            points[0].X -= lastc.X - c.X;
                            points[0].Y -= lastc.Y - c.Y;
                        }
                        builder.CubicBezierTo(points[0], points[1], points[2]);
                        lastc = points[1];
                        c = points[2];
                        break;
                    case 'Q': // Quadratic Bezier Curve
                        data = FindPoints(data, points, 2, relative, c);
                        builder.QuadraticBezierTo(points[0], points[1]);
                        lastc = points[0];
                        c = points[1];
                        break;
                    case 'T':
                        data = FindPoints(data, points.Slice(1), 1, relative, c);
                        points[0] = c;
                        if (previousOp is 'Q' or 'T')
                        {
                            points[0].X -= lastc.X - c.X;
                            points[0].Y -= lastc.Y - c.Y;
                        }

                        builder.QuadraticBezierTo(points[0], points[1]);
                        lastc = points[0];
                        c = points[1];
                        break;
                    case 'A':
                    {
                        data = FindScaler(data, out float radiiX);
                        data = TrimSeperator(data);
                        data = FindScaler(data, out float radiiY);
                        data = TrimSeperator(data);
                        data = FindScaler(data, out float angle);
                        data = TrimSeperator(data);
                        data = FindScaler(data, out float largeArc);
                        data = TrimSeperator(data);
                        data = FindScaler(data, out float sweep);

                        data = FindPoint(data, out var point, relative, c);
                        if (data.Length > 0)
                        {
                            builder.ArcTo(radiiX, radiiY, angle, largeArc == 1, sweep == 1, point);
                            c = point;
                        }
                    }
                    break;
                    case 'Z':
                        builder.CloseFigure();
                        c = first;
                        break;
                    case '~':
                    {
                        SkPoint args[2];
                        data = find_points(data, args, 2, false, nullptr);
                        path.moveTo(args[0].fX, args[0].fY);
                        path.lineTo(args[1].fX, args[1].fY);
                    }
                    break;
                    default:
                        return false;
                }
                if (previousOp == 0)
                {
                    first = c;
                }
                previousOp = op;
            }

            return true;

            static bool IsSeperator(char ch)
                => char.IsWhiteSpace(ch) || ch == ',';

            static ReadOnlySpan<char> TrimSeperator(ReadOnlySpan<char> data)
            {
                if (data.Length == 0)
                {
                    return data;
                }

                int idx = 0;
                for (; idx < data.Length; idx++)
                {
                    if (!IsSeperator(data[idx]))
                    {
                        break;
                    }
                }

                return data.Slice(idx);
            }


            static ReadOnlySpan<char> FindPoint(ReadOnlySpan<char> str, out PointF value, bool isRelative, in PointF relative)
            {
                str = FindScaler(str, out float x);
                str = FindScaler(str, out float y);
                if (isRelative)
                {
                    x += relative.X;
                    y += relative.Y;
                }

                value = new PointF(x, y);
                return str;
            }

            static ReadOnlySpan<char> FindPoints(ReadOnlySpan<char> str, Span<PointF> value, int count, bool isRelative, in PointF relative)
            {
                for (int i = 0; i < value.Length && i < count; i++)
                {
                    str = FindPoint(str, out value[i], isRelative, relative);
                }

                return str;
            }

            static ReadOnlySpan<char> FindScaler(ReadOnlySpan<char> str, out float scaler)
            {
                str = str.TrimStart();
                scaler = 0;

                for (var i = 0; i < str.Length; i++)
                {
                    if (IsSeperator(str[i]))
                    {
                        scaler = float.Parse(str.Slice(0, i));
                        str = str.Slice(i);
                    }
                }

                // we concumed eveything
                return ReadOnlySpan<char>.Empty;
            }
        }
    }
}
