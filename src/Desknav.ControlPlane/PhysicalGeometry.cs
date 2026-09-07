namespace Desknav.ControlPlane;

public readonly record struct PhysicalPixels
{
    private readonly long _value;

    public PhysicalPixels(int value)
    {
        _value = value;
    }

    private PhysicalPixels(long value)
    {
        _value = value;
    }

    public static explicit operator int(PhysicalPixels value) =>
        checked((int)value._value);

    public static double operator /(
        PhysicalPixels value,
        double divisor) =>
        value._value / divisor;

    public static PhysicalPixels operator +(
        PhysicalPixels left,
        PhysicalPixels right) =>
        new(left._value + right._value);

    public static PhysicalPixels operator -(
        PhysicalPixels left,
        PhysicalPixels right) =>
        new(left._value - right._value);

    public static bool operator <(
        PhysicalPixels left,
        PhysicalPixels right) =>
        left._value < right._value;

    public static bool operator <=(
        PhysicalPixels left,
        PhysicalPixels right) =>
        left._value <= right._value;

    public static bool operator >(
        PhysicalPixels left,
        PhysicalPixels right) =>
        left._value > right._value;

    public static bool operator >=(
        PhysicalPixels left,
        PhysicalPixels right) =>
        left._value >= right._value;

    internal static PhysicalPixels Min(
        PhysicalPixels left,
        PhysicalPixels right) =>
        left <= right ? left : right;

    internal static PhysicalPixels Max(
        PhysicalPixels left,
        PhysicalPixels right) =>
        left >= right ? left : right;

    internal static long Area(
        PhysicalPixels width,
        PhysicalPixels height) =>
        width._value * height._value;

    internal int CompareTo(PhysicalPixels other) =>
        _value.CompareTo(other._value);
}

public readonly record struct PhysicalPoint(
    PhysicalPixels X,
    PhysicalPixels Y)
{
    public PhysicalPoint(int x, int y)
        : this(new PhysicalPixels(x), new PhysicalPixels(y))
    {
    }

    public static PhysicalOffset operator -(
        PhysicalPoint left,
        PhysicalPoint right) =>
        new(left.X - right.X, left.Y - right.Y);

    public PhysicalPoint ClampToMinimum(PhysicalPoint minimum) =>
        new(
            PhysicalPixels.Max(X, minimum.X),
            PhysicalPixels.Max(Y, minimum.Y));
}

public readonly record struct PhysicalOffset(
    PhysicalPixels X,
    PhysicalPixels Y);

public readonly record struct PhysicalSize
{
    public PhysicalSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = new PhysicalPixels(width);
        Height = new PhysicalPixels(height);
    }

    public PhysicalPixels Width { get; }

    public PhysicalPixels Height { get; }
}

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

    public PhysicalPoint Origin { get; }

    public PhysicalSize Size { get; }

    public PhysicalPixels Left => Origin.X;

    public PhysicalPixels Top => Origin.Y;

    public PhysicalPixels Width => Size.Width;

    public PhysicalPixels Height => Size.Height;

    private PhysicalPixels Right => Left + Width;

    private PhysicalPixels Bottom => Top + Height;

    public bool Contains(PhysicalPoint point)
        =>
        point.X >= Left
        && point.X < Right
        && point.Y >= Top
        && point.Y < Bottom;

    public long IntersectionArea(PhysicalRect other)
    {
        var left = PhysicalPixels.Max(Left, other.Left);
        var top = PhysicalPixels.Max(Top, other.Top);
        var right = PhysicalPixels.Min(Right, other.Right);
        var bottom = PhysicalPixels.Min(Bottom, other.Bottom);
        var width = PhysicalPixels.Max(default, right - left);
        var height = PhysicalPixels.Max(default, bottom - top);
        return PhysicalPixels.Area(width, height);
    }

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