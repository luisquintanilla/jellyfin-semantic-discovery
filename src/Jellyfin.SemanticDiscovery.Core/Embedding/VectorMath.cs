using System.Numerics.Tensors;

namespace Jellyfin.SemanticDiscovery.Core.Embedding;

public static class VectorMath
{
    public static void Normalize(Span<float> vector)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("A vector cannot be empty.", nameof(vector));
        }

        float norm = TensorPrimitives.Norm(vector);
        if (!float.IsFinite(norm) || norm <= 0F)
        {
            throw new InvalidDataException("Cannot normalize a non-finite or zero vector.");
        }

        TensorPrimitives.Divide(vector, norm, vector);
    }

    public static float CosineSimilarity(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.IsEmpty)
        {
            throw new ArgumentException("Vectors must have equal, non-zero dimensions.");
        }

        float leftNorm = TensorPrimitives.Norm(left);
        float rightNorm = TensorPrimitives.Norm(right);
        if (!float.IsFinite(leftNorm) || !float.IsFinite(rightNorm))
        {
            throw new InvalidDataException(
                "Cannot compare vectors with non-finite norms.");
        }

        if (leftNorm <= 0F || rightNorm <= 0F)
        {
            return 0F;
        }

        float dot = TensorPrimitives.Dot(left, right);
        if (!float.IsFinite(dot))
        {
            throw new InvalidDataException(
                "Cannot compare vectors with a non-finite dot product.");
        }

        float similarity = dot / (leftNorm * rightNorm);
        return float.IsFinite(similarity)
            ? similarity
            : throw new InvalidDataException(
                "Cannot produce a non-finite cosine similarity.");
    }
}
