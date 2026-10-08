using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.Classes
{

    /// </summary>
    public class OpeningSchemeHeader
    {
        public string FileType { get; set; } = string.Empty;
        public int Version { get; set; }
        public int AiracCycle { get; set; }
        public DateTime AiracStartDate { get; set; }
        public DateTime AiracEndDate { get; set; }
        public int RecordCount { get; set; }
        public string DataSource { get; set; } = string.Empty;
    }


    /// </summary>
    public class OpeningSchemeRecord
    {
        public DateTime Date { get; set; }
        public string AccName { get; set; } = string.Empty;
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string ConfigurationName { get; set; } = string.Empty;
        public char Origin { get; set; } // 'E' = Env, 'T' = Tact[cite: 1]

        // Absolute interval in which the configuration is active. The file gives minute resolution and the next record
        // starts the minute after End (04:14 -> 04:15), so End is EXCLUSIVE: it already includes the whole last minute.
        public DateTime Start { get { return Date + StartTime; } }
        public DateTime End { get { return Date + EndTime + TimeSpan.FromMinutes(1); } }
    }


    /// </summary>
    public class OpeningScheme
    {
        public OpeningSchemeHeader Header { get; set; } = new OpeningSchemeHeader();
        public List<OpeningSchemeRecord> Records { get; set; } = new List<OpeningSchemeRecord>();

        // Records of each ACC sorted by Start, so "which configuration is open at time t" is a binary search. Filled by BuildIndex()
        private readonly Dictionary<string, List<OpeningSchemeRecord>> _byAcc =
            new Dictionary<string, List<OpeningSchemeRecord>>(StringComparer.OrdinalIgnoreCase);

        /// ACCs that have at least one record
        public IEnumerable<string> AccNames { get { return _byAcc.Keys; } }

        /// Limits of the whole scheme, for the range of the time bar
        public DateTime FirstStart { get; private set; }
        public DateTime LastEnd { get; private set; }

        /// Call once after Records is filled (the reader does it)
        public void BuildIndex()
        {
            _byAcc.Clear();
            FirstStart = DateTime.MaxValue;
            LastEnd = DateTime.MinValue;

            for (int i = 0; i < Records.Count; i++)
            {
                OpeningSchemeRecord r = Records[i];
                if (!_byAcc.TryGetValue(r.AccName, out List<OpeningSchemeRecord> list))
                {
                    list = new List<OpeningSchemeRecord>();
                    _byAcc[r.AccName] = list;
                }
                list.Add(r);

                if (r.Start < FirstStart) FirstStart = r.Start;
                if (r.End > LastEnd) LastEnd = r.End;
            }

            foreach (List<OpeningSchemeRecord> list in _byAcc.Values)
                list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        /// Configuration name open in the ACC at that instant, or null if the ACC has no record covering it
        public string GetConfigurationName(string accName, DateTime time)
        {
            if (!_byAcc.TryGetValue(accName, out List<OpeningSchemeRecord> list))
                return null;

            // Last record with Start <= time
            int lo = 0, hi = list.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Start <= time) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            if (found >= 0 && time < list[found].End)
                return list[found].ConfigurationName;
            return null; // before the first record, or in a gap
        }

        /// ACC -> configuration name open at that instant, for every ACC that has one (ACCs without a record at t are left out)
        public Dictionary<string, string> GetActiveConfigurations(DateTime time)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string acc in _byAcc.Keys)
            {
                string name = GetConfigurationName(acc, time);
                if (name != null)
                    result[acc] = name;
            }
            return result;
        }
    }
}