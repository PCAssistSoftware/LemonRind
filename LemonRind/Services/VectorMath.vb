Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>
    ''' Small, dependency-free helpers for the hand-rolled brute-force vector
    ''' search shape (embed → store → cosine similarity), shared between
    ''' long-term memory and Knowledge Bases rather than each building its
    ''' own copy.
    ''' </summary>
    Public Module VectorMath

        ''' <summary>
        ''' Serializes a float vector to bytes for SQLite BLOB storage.
        ''' MemoryMarshal.Cast reinterprets the existing float memory as
        ''' bytes directly, avoiding the intermediate Single() copy a
        ''' vector.ToArray() + Buffer.BlockCopy approach would need.
        ''' </summary>
        Public Function ToBytes(vector As ReadOnlyMemory(Of Single)) As Byte()
            Return MemoryMarshal.Cast(Of Single, Byte)(vector.Span).ToArray()
        End Function

        ''' <summary>The inverse of ToBytes - reconstructs the float vector from a stored BLOB.</summary>
        Public Function FromBytes(bytes As Byte()) As Single()
            Dim vector(bytes.Length \ 4 - 1) As Single
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length)
            Return vector
        End Function

        ''' <summary>
        ''' Standard cosine similarity, -1 (opposite) to 1 (identical
        ''' direction) - higher means more semantically similar. Assumes
        ''' both vectors are the same length (the same embedding model
        ''' produced both), which is true for everything this app embeds.
        ''' </summary>
        Public Function CosineSimilarity(a As Single(), b As Single()) As Double
            Dim dotProduct As Double = 0
            Dim magnitudeA As Double = 0
            Dim magnitudeB As Double = 0

            For i = 0 To a.Length - 1
                dotProduct += a(i) * b(i)
                magnitudeA += a(i) * a(i)
                magnitudeB += b(i) * b(i)
            Next

            If magnitudeA = 0 OrElse magnitudeB = 0 Then Return 0
            Return dotProduct / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB))
        End Function

    End Module

End Namespace
