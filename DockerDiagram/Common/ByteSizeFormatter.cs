using System;
using System.Globalization;

namespace DockerDiagram.Common
{
    /// <summary>
    /// 바이트 값을 1024 단위로 축약합니다. 0이나 음수의 도메인 의미는 호출자가 결정합니다.
    /// </summary>
    public static class ByteSizeFormatter
    {
        private static readonly string[] MetricUnits = ["B", "KB", "MB", "GB", "TB"];
        private static readonly string[] IecUnits = ["B", "KiB", "MiB", "GiB", "TiB"];

        public static string FormatMetric(
            long bytes,
            bool spaceBeforeUnit = true,
            int minimumFractionDigits = 0,
            int maximumFractionDigits = 2,
            double wholeNumberThreshold = double.PositiveInfinity) =>
            FormatCore(
                Math.Max(0, bytes),
                MetricUnits,
                spaceBeforeUnit,
                minimumFractionDigits,
                maximumFractionDigits,
                wholeNumberThreshold);

        public static string FormatMetric(
            ulong bytes,
            bool spaceBeforeUnit = true,
            int minimumFractionDigits = 0,
            int maximumFractionDigits = 2,
            double wholeNumberThreshold = double.PositiveInfinity) =>
            FormatCore(
                bytes,
                MetricUnits,
                spaceBeforeUnit,
                minimumFractionDigits,
                maximumFractionDigits,
                wholeNumberThreshold);

        public static string FormatIec(
            long bytes,
            bool spaceBeforeUnit = true,
            int minimumFractionDigits = 0,
            int maximumFractionDigits = 2,
            double wholeNumberThreshold = double.PositiveInfinity) =>
            FormatCore(
                Math.Max(0, bytes),
                IecUnits,
                spaceBeforeUnit,
                minimumFractionDigits,
                maximumFractionDigits,
                wholeNumberThreshold);

        private static string FormatCore(
            double bytes,
            string[] units,
            bool spaceBeforeUnit,
            int minimumFractionDigits,
            int maximumFractionDigits,
            double wholeNumberThreshold)
        {
            minimumFractionDigits = Math.Max(0, minimumFractionDigits);
            maximumFractionDigits = Math.Max(minimumFractionDigits, maximumFractionDigits);

            double value = bytes;
            int unitIndex = 0;
            while (value >= 1024 && unitIndex < units.Length - 1)
            {
                value /= 1024;
                unitIndex++;
            }

            string numberFormat = unitIndex == 0 || value >= wholeNumberThreshold
                ? "0"
                : BuildNumberFormat(minimumFractionDigits, maximumFractionDigits);
            string separator = spaceBeforeUnit ? " " : string.Empty;
            return value.ToString(numberFormat, CultureInfo.InvariantCulture) + separator + units[unitIndex];
        }

        private static string BuildNumberFormat(int minimumFractionDigits, int maximumFractionDigits)
        {
            if (maximumFractionDigits == 0) return "0";

            return "0." +
                new string('0', minimumFractionDigits) +
                new string('#', maximumFractionDigits - minimumFractionDigits);
        }
    }
}
