namespace Desknav.ControlPlane;

/// <summary>
/// A point in the global desktop coordinate space, measured in physical pixels.
/// </summary>
public readonly record struct PhysicalPoint(int X, int Y)
{
    /// <summary>
    /// Returns the origin-independent displacement from <paramref name="right"/>
    /// to <paramref name="left"/>.
    /// </summary>
    public static PhysicalVector operator -(
        PhysicalPoint left,
        PhysicalPoint right) =>
        new(
            (long)left.X - right.X,
            (long)left.Y - right.Y);

    /// <summary>
    /// Clamps each coordinate to the corresponding minimum coordinate.
    /// </summary>
    public PhysicalPoint ClampToMinimum(PhysicalPoint minimum) =>
        new(
            Math.Max(X, minimum.X),
            Math.Max(Y, minimum.Y));
}

/// <summary>
/// An origin-independent displacement measured in physical pixels.
/// </summary>
public readonly record struct PhysicalVector(long X, long Y);

/// <summary>
/// An origin-independent extent measured in physical pixels.
/// </summary>
public readonly record struct PhysicalSize
{
    /// <summary>
    /// Creates a positive physical-pixel extent.
    /// </summary>
    public PhysicalSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

/// <summary>
/// An axis-aligned rectangle in the global desktop coordinate space,
/// measured in physical pixels.
/// </summary>
public readonly record struct PhysicalRect
    : IComparable<PhysicalRect>
{
    public PhysicalRect(int left, int top, int width, int height)
        : this(
            new PhysicalPoint(left, top),
            new PhysicalSize(width, height))
    {
    }

    public PhysicalRect(PhysicalPoint origin, PhysicalSize size)
    {
        if (size == default)
        {
            throw new ArgumentException(
                "Physical size must be initialized.",
                nameof(size));
        }

        Origin = origin;
        Size = size;
    }

    /// <summary>
    /// Gets the rectangle's top-left point in global desktop coordinates.
    /// </summary>
    public PhysicalPoint Origin { get; }

    /// <summary>
    /// Gets the rectangle's origin-independent extent.
    /// </summary>
    public PhysicalSize Size { get; }

    public int Left => Origin.X;

    public int Top => Origin.Y;

    public int Width => Size.Width;

    public int Height => Size.Height;

    private long Right => (long)Left + Width;

    private long Bottom => (long)Top + Height;

    /// <summary>
    /// Determines whether this rectangle contains a global physical point.
    /// The right and bottom edges are exclusive.
    /// </summary>
    public bool Contains(PhysicalPoint point)
        =>
        point.X >= Left
        && point.X < Right
        && point.Y >= Top
        && point.Y < Bottom;

    /// <summary>
    /// Returns the overlapping area in square physical pixels.
    /// </summary>
    public long IntersectionArea(PhysicalRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        return width * height;
    }

    /// <summary>
    /// Orders rectangles by top, left, width, then height.
    /// </summary>
    public int CompareTo(PhysicalRect other)
    {
        var top = Top.CompareTo(other.Top);
        if (top != 0)
        {
            return top;
        }

        var left = Left.CompareTo(other.Left);
        if (left != 0)
        {
            return left;
        }

        var width = Width.CompareTo(other.Width);
        return width != 0
            ? width
            : Height.CompareTo(other.Height);
    }
}