using System;
using System.Text;

namespace RFIDTiming.Domain
{
    public static class ChipIdNormalizer
    {
        public static string Normalize(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return String.Empty;

            var clean = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (!Char.IsWhiteSpace(character))
                    clean.Append(Char.ToUpperInvariant(character));
            }

            string normalized = clean.ToString();
            bool numeric = true;
            for (int index = 0; index < normalized.Length; index++)
            {
                if (normalized[index] < '0' || normalized[index] > '9')
                {
                    numeric = false;
                    break;
                }
            }

            // El lector entrega los EPC decimales con relleno de ceros a la izquierda.
            // Los identificadores alfanuméricos conservan sus ceros iniciales.
            if (!numeric) return normalized;
            string withoutPadding = normalized.TrimStart('0');
            return withoutPadding.Length == 0 ? "0" : withoutPadding;
        }
    }
}
