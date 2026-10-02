using System.Runtime.InteropServices;

namespace LemonRindBlazor.Services;

/// <summary>
/// Small, dependency-free helpers for the hand-rolled brute-force vector
/// search shape (embed → store → cosine similarity). Ported directly from
/// the VB.NET/WPF LemonRind app's Services\VectorMath.vb.
/// </summary>
public static class VectorMath
{
    /// <summary>Serializes a float vector to bytes for SQLite BLOB storage - reinterprets the existing memory directly, no intermediate copy.</summary>
    public static byte[] ToBytes(ReadOnlyMemory<float> vector)
        => MemoryMarshal.Cast<float, byte>(vector.Span).ToArray();

    /// <summary>The inverse of ToBytes - reconstructs the float vector from a stored BLOB.</summary>
    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    /// <summary>Standard cosine similarity, -1 (opposite) to 1 (identical direction) - higher means more semantically similar.</summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        double dotProduct = 0, magnitudeA = 0, magnitudeB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        if (magnitudeA == 0 || magnitudeB == 0) return 0;
        return dotProduct / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
    }
}
