using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Hello_World
{
    /// <summary>
    /// WeldingScoreCalculator
    /// - 용접 각도/속도/거리 입력(콘솔 또는 CSV)을 받아 100점 만점 점수를 계산하고 파일로 저장합니다.
    ///
    /// 기본값(필요시 CLI로 변경 가능):
    /// - 각도(target=45deg, idealTol=5deg, maxTol=20deg, weight=0.4)
    /// - 속도(target=5mm/s, idealTol=0.5mm/s, maxTol=2mm/s, weight=0.3)
    /// - 거리(target=10mm, idealTol=1mm, maxTol=5mm, weight=0.3)
    ///
    /// 점수 모델:
    /// - deviation = |actual - target|
    /// - deviation <= idealTolerance -> 해당 항목 만점
    /// - deviation >= maxTolerance   -> 해당 항목 0점
    /// - 그 사이 구간은 선형 감점
    /// </summary>
    public static class WeldingScoreCalculator
    {
        public sealed class ScoreConfig
        {
            public MetricConfig Angle { get; set; } = new MetricConfig
            {
                Name = "angle_deg",
                Target = 45.0,
                IdealTolerance = 5.0,
                MaxTolerance = 20.0,
                Weight = 0.4
            };

            public MetricConfig Speed { get; set; } = new MetricConfig
            {
                Name = "speed_mm_s",
                Target = 5.0,
                IdealTolerance = 0.5,
                MaxTolerance = 2.0,
                Weight = 0.3
            };

            public MetricConfig Distance { get; set; } = new MetricConfig
            {
                Name = "distance_mm",
                Target = 10.0,
                IdealTolerance = 1.0,
                MaxTolerance = 5.0,
                Weight = 0.3
            };

            public void NormalizeWeights()
            {
                double sum = Angle.Weight + Speed.Weight + Distance.Weight;
                if (sum <= 0) return;
                Angle.Weight /= sum;
                Speed.Weight /= sum;
                Distance.Weight /= sum;
            }
        }

        public sealed class MetricConfig
        {
            public string Name { get; set; } = "";
            public double Target { get; set; }
            public double IdealTolerance { get; set; }
            public double MaxTolerance { get; set; }
            public double Weight { get; set; }
        }

        public sealed class WeldingMeasurement
        {
            public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
            public double AngleDeg { get; set; }
            public double SpeedMmPerSec { get; set; }
            public double DistanceMm { get; set; }
            public string Operator { get; set; } = "";
            public string Notes { get; set; } = "";
        }

        public sealed class WeldingScoreResult
        {
            public WeldingMeasurement Measurement { get; set; } = new WeldingMeasurement();
            public double TotalScore { get; set; }
            public double AngleScore { get; set; }
            public double SpeedScore { get; set; }
            public double DistanceScore { get; set; }
            public double AngleDeviation { get; set; }
            public double SpeedDeviation { get; set; }
            public double DistanceDeviation { get; set; }
        }

        public static int Run(string[] args)
        {
            var config = new ScoreConfig();
            config.NormalizeWeights();

            string? inputCsv = null;
            string outCsv = "welding_scores.csv";
            string? outJsonl = null;
            string? op = null;
            string? notes = null;
            double? angle = null, speed = null, distance = null;

            // 간단한 CLI 파서 (의존성 없이 동작)
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string NextValue()
                {
                    if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for '{a}'");
                    return args[++i];
                }

                switch (a)
                {
                    case "--input":
                        inputCsv = NextValue();
                        break;
                    case "--out":
                        outCsv = NextValue();
                        break;
                    case "--out-jsonl":
                        outJsonl = NextValue();
                        break;
                    case "--operator":
                        op = NextValue();
                        break;
                    case "--notes":
                        notes = NextValue();
                        break;

                    case "--angle":
                        angle = ParseDoubleInvariant(NextValue());
                        break;
                    case "--speed":
                        speed = ParseDoubleInvariant(NextValue());
                        break;
                    case "--distance":
                        distance = ParseDoubleInvariant(NextValue());
                        break;

                    // 목표/허용오차 튜닝 (선택)
                    case "--angle-target":
                        config.Angle.Target = ParseDoubleInvariant(NextValue());
                        break;
                    case "--angle-ideal-tol":
                        config.Angle.IdealTolerance = ParseDoubleInvariant(NextValue());
                        break;
                    case "--angle-max-tol":
                        config.Angle.MaxTolerance = ParseDoubleInvariant(NextValue());
                        break;

                    case "--speed-target":
                        config.Speed.Target = ParseDoubleInvariant(NextValue());
                        break;
                    case "--speed-ideal-tol":
                        config.Speed.IdealTolerance = ParseDoubleInvariant(NextValue());
                        break;
                    case "--speed-max-tol":
                        config.Speed.MaxTolerance = ParseDoubleInvariant(NextValue());
                        break;

                    case "--distance-target":
                        config.Distance.Target = ParseDoubleInvariant(NextValue());
                        break;
                    case "--distance-ideal-tol":
                        config.Distance.IdealTolerance = ParseDoubleInvariant(NextValue());
                        break;
                    case "--distance-max-tol":
                        config.Distance.MaxTolerance = ParseDoubleInvariant(NextValue());
                        break;

                    case "--weights":
                        // 예: --weights 0.4,0.3,0.3  (angle,speed,distance)
                        var parts = NextValue().Split(',');
                        if (parts.Length != 3) throw new ArgumentException("Expected 3 comma-separated weights: angle,speed,distance");
                        config.Angle.Weight = ParseDoubleInvariant(parts[0]);
                        config.Speed.Weight = ParseDoubleInvariant(parts[1]);
                        config.Distance.Weight = ParseDoubleInvariant(parts[2]);
                        config.NormalizeWeights();
                        break;

                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                }
            }

            List<WeldingMeasurement> measurements;
            if (!string.IsNullOrWhiteSpace(inputCsv))
            {
                measurements = LoadMeasurementsFromCsv(inputCsv!, op, notes);
            }
            else if (angle.HasValue && speed.HasValue && distance.HasValue)
            {
                measurements = new List<WeldingMeasurement>
                {
                    new WeldingMeasurement
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        AngleDeg = angle.Value,
                        SpeedMmPerSec = speed.Value,
                        DistanceMm = distance.Value,
                        Operator = op ?? "",
                        Notes = notes ?? ""
                    }
                };
            }
            else
            {
                // 콘솔 인터랙티브 입력
                measurements = new List<WeldingMeasurement> { PromptMeasurementFromConsole(op, notes) };
            }

            var results = measurements.Select(m => Calculate(m, config)).ToList();

            SaveResultsToCsv(outCsv, results);
            if (!string.IsNullOrWhiteSpace(outJsonl))
            {
                SaveResultsToJsonl(outJsonl!, results);
            }

            // 요약 출력
            foreach (var r in results)
            {
                Console.WriteLine(
                    $"[{r.Measurement.Timestamp:O}] total={r.TotalScore:0.0} " +
                    $"(angle={r.AngleScore:0.0}, speed={r.SpeedScore:0.0}, distance={r.DistanceScore:0.0})");
            }
            Console.WriteLine($"Saved CSV -> {outCsv}");
            if (!string.IsNullOrWhiteSpace(outJsonl)) Console.WriteLine($"Saved JSONL -> {outJsonl}");

            return 0;
        }

        public static WeldingScoreResult Calculate(WeldingMeasurement m, ScoreConfig config)
        {
            config.NormalizeWeights();

            double angleDev = Math.Abs(m.AngleDeg - config.Angle.Target);
            double speedDev = Math.Abs(m.SpeedMmPerSec - config.Speed.Target);
            double distDev = Math.Abs(m.DistanceMm - config.Distance.Target);

            double angleScore = ScoreMetric(angleDev, config.Angle) * 100.0;
            double speedScore = ScoreMetric(speedDev, config.Speed) * 100.0;
            double distScore = ScoreMetric(distDev, config.Distance) * 100.0;

            double total = angleScore + speedScore + distScore;
            total = Clamp(total, 0.0, 100.0);

            return new WeldingScoreResult
            {
                Measurement = m,
                TotalScore = Math.Round(total, 1),
                AngleScore = Math.Round(angleScore, 1),
                SpeedScore = Math.Round(speedScore, 1),
                DistanceScore = Math.Round(distScore, 1),
                AngleDeviation = Math.Round(angleDev, 4),
                SpeedDeviation = Math.Round(speedDev, 4),
                DistanceDeviation = Math.Round(distDev, 4),
            };
        }

        /// <summary>
        /// 각 지표의 점수를 [0..weight] 범위로 반환.
        /// </summary>
        private static double ScoreMetric(double deviation, MetricConfig cfg)
        {
            if (cfg.MaxTolerance <= cfg.IdealTolerance) return deviation <= cfg.IdealTolerance ? cfg.Weight : 0.0;

            double normalized;
            if (deviation <= cfg.IdealTolerance) normalized = 1.0;
            else if (deviation >= cfg.MaxTolerance) normalized = 0.0;
            else
            {
                double span = (cfg.MaxTolerance - cfg.IdealTolerance);
                double t = (deviation - cfg.IdealTolerance) / span; // 0..1
                normalized = 1.0 - t;
            }

            return cfg.Weight * Clamp(normalized, 0.0, 1.0);
        }

        private static WeldingMeasurement PromptMeasurementFromConsole(string? op, string? notes)
        {
            Console.Write("Angle (deg): ");
            double angle = ParseDoubleInvariant(Console.ReadLine());
            Console.Write("Speed (mm/s): ");
            double speed = ParseDoubleInvariant(Console.ReadLine());
            Console.Write("Distance (mm): ");
            double distance = ParseDoubleInvariant(Console.ReadLine());

            if (string.IsNullOrWhiteSpace(op))
            {
                Console.Write("Operator (optional): ");
                op = Console.ReadLine();
            }
            if (string.IsNullOrWhiteSpace(notes))
            {
                Console.Write("Notes (optional): ");
                notes = Console.ReadLine();
            }

            return new WeldingMeasurement
            {
                Timestamp = DateTimeOffset.UtcNow,
                AngleDeg = angle,
                SpeedMmPerSec = speed,
                DistanceMm = distance,
                Operator = op ?? "",
                Notes = notes ?? ""
            };
        }

        private static List<WeldingMeasurement> LoadMeasurementsFromCsv(string path, string? defaultOperator, string? defaultNotes)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Input CSV not found", path);

            var lines = File.ReadAllLines(path);
            var result = new List<WeldingMeasurement>();

            foreach (var raw in lines)
            {
                var line = raw?.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("#")) continue;

                // 헤더 자동 스킵
                if (line.IndexOf("angle", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("speed", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    line.IndexOf("distance", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                // CSV: angle_deg,speed_mm_s,distance_mm,operator,notes
                var cols = SplitCsvSimple(line);
                if (cols.Count < 3) continue;

                var m = new WeldingMeasurement
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    AngleDeg = ParseDoubleInvariant(cols[0]),
                    SpeedMmPerSec = ParseDoubleInvariant(cols[1]),
                    DistanceMm = ParseDoubleInvariant(cols[2]),
                    Operator = cols.Count >= 4 && !string.IsNullOrWhiteSpace(cols[3]) ? cols[3] : (defaultOperator ?? ""),
                    Notes = cols.Count >= 5 && !string.IsNullOrWhiteSpace(cols[4]) ? cols[4] : (defaultNotes ?? "")
                };

                result.Add(m);
            }

            return result;
        }

        private static void SaveResultsToCsv(string path, List<WeldingScoreResult> results)
        {
            bool exists = File.Exists(path);

            var sb = new StringBuilder();
            if (!exists)
            {
                sb.AppendLine("timestamp,angle_deg,speed_mm_s,distance_mm,operator,notes,total_score,angle_score,speed_score,distance_score,angle_deviation,speed_deviation,distance_deviation");
            }

            foreach (var r in results)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(r.Measurement.Timestamp.ToString("O")),
                    r.Measurement.AngleDeg.ToString(CultureInfo.InvariantCulture),
                    r.Measurement.SpeedMmPerSec.ToString(CultureInfo.InvariantCulture),
                    r.Measurement.DistanceMm.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(r.Measurement.Operator),
                    CsvEscape(r.Measurement.Notes),
                    r.TotalScore.ToString(CultureInfo.InvariantCulture),
                    r.AngleScore.ToString(CultureInfo.InvariantCulture),
                    r.SpeedScore.ToString(CultureInfo.InvariantCulture),
                    r.DistanceScore.ToString(CultureInfo.InvariantCulture),
                    r.AngleDeviation.ToString(CultureInfo.InvariantCulture),
                    r.SpeedDeviation.ToString(CultureInfo.InvariantCulture),
                    r.DistanceDeviation.ToString(CultureInfo.InvariantCulture)
                ));
            }

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void SaveResultsToJsonl(string path, List<WeldingScoreResult> results)
        {
            // 외부 JSON 라이브러리 없이 최소 JSONL 저장
            // 각 라인은 독립적인 JSON 객체.
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                sb.AppendLine("{"
                    + $"\"timestamp\":\"{JsonEscape(r.Measurement.Timestamp.ToString("O"))}\","
                    + $"\"angle_deg\":{r.Measurement.AngleDeg.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"speed_mm_s\":{r.Measurement.SpeedMmPerSec.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"distance_mm\":{r.Measurement.DistanceMm.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"operator\":\"{JsonEscape(r.Measurement.Operator)}\","
                    + $"\"notes\":\"{JsonEscape(r.Measurement.Notes)}\","
                    + $"\"total_score\":{r.TotalScore.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"angle_score\":{r.AngleScore.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"speed_score\":{r.SpeedScore.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"distance_score\":{r.DistanceScore.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"angle_deviation\":{r.AngleDeviation.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"speed_deviation\":{r.SpeedDeviation.ToString(CultureInfo.InvariantCulture)},"
                    + $"\"distance_deviation\":{r.DistanceDeviation.ToString(CultureInfo.InvariantCulture)}"
                    + "}");
            }

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("WeldingScoreCalculator");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  Interactive:");
            Console.WriteLine("    (no args) -> prompts for angle/speed/distance");
            Console.WriteLine();
            Console.WriteLine("  Single measurement:");
            Console.WriteLine("    --angle <deg> --speed <mm/s> --distance <mm> [--operator <name>] [--notes <text>] [--out <csv>] [--out-jsonl <jsonl>]");
            Console.WriteLine();
            Console.WriteLine("  Batch from CSV:");
            Console.WriteLine("    --input <measurements.csv> [--out <csv>] [--out-jsonl <jsonl>] [--operator <default>] [--notes <default>]");
            Console.WriteLine();
            Console.WriteLine("  Tuning (optional):");
            Console.WriteLine("    --angle-target <v> --angle-ideal-tol <v> --angle-max-tol <v>");
            Console.WriteLine("    --speed-target <v> --speed-ideal-tol <v> --speed-max-tol <v>");
            Console.WriteLine("    --distance-target <v> --distance-ideal-tol <v> --distance-max-tol <v>");
            Console.WriteLine("    --weights <angle,speed,distance>");
        }

        private static double ParseDoubleInvariant(string? s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            s = s.Trim();
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
            // 한국/유럽식 소수점 콤마 입력 보정
            var normalized = s.Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            throw new FormatException($"Invalid number: '{s}'");
        }

        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static string CsvEscape(string? s)
        {
            s ??= "";
            bool mustQuote = s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r");
            if (!mustQuote) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string JsonEscape(string? s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(ch))
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 아주 단순한 CSV splitter:
        /// - 쌍따옴표로 감싼 필드 내 콤마 지원
        /// - 이스케이프 "" 지원
        /// </summary>
        private static List<string> SplitCsvSimple(string line)
        {
            var cols = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // "" -> "
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == ',')
                    {
                        cols.Add(sb.ToString().Trim());
                        sb.Clear();
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }

            cols.Add(sb.ToString().Trim());
            return cols;
        }
    }
}
