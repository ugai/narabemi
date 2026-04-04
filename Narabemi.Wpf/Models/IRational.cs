namespace Narabemi.Models
{
    public interface IRational<T>
    {
        T Numerator { get; }
        T Denominator { get; }
    }
}
