using System;
using System.Collections.Generic;
using System.Threading;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.NTS
{
    // Turns the elementary-sector intervals of ONE flight (CrossingCalculator.Cross) into entries, at two levels:
    //   ElementaryEntries: one entry per stay in each elementary sector, opening scheme ignored (lets you later filter
    //                      down to individual elementary sectors and see them over time).
    //   OpenEntries:       entries into the sectors (elementary or collapsed) that the opening scheme has OPEN.
    // Order of the project parameters: stays are joined with ReentryTime first, then the time actually spent inside
    // (inside the open period, for OpenEntries) must be >= MinTimeInSector.
    public class EntryCalculator
    {
        // Period [Start, End) in which a sector is open in one ACC. Consecutive records that keep the sector open are
        // joined, even if the configuration name changes, so a change that keeps the sector open does not split a stay
        private class OpenPeriod
        {
            public string Acc;
            public DateTime Start;
            public DateTime End;
        }

        private readonly OpeningScheme _scheme;

        // Sector of any configuration (elementary name or collapsed id) -> elementary sectors it is made of
        private readonly Dictionary<string, HashSet<string>> _members =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Elementary sector -> sectors of any configuration that contain it (itself and/or collapsed sectors)
        private readonly Dictionary<string, List<string>> _sectorsOfElementary =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // "ACC;CONFIG" -> sectors of that configuration
        private readonly Dictionary<string, HashSet<string>> _configSectors =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Sector -> periods in which it is open (any ACC)
        private readonly Dictionary<string, List<OpenPeriod>> _openPeriods =
            new Dictionary<string, List<OpenPeriod>>(StringComparer.OrdinalIgnoreCase);

        // Counters of the OpenEntries calls (for checking the data). Interlocked: ComputeEntries calls it from several threads
        private int _staysNeverOpen, _piecesTooShort;
        public int StaysNeverOpen { get { return _staysNeverOpen; } }   // stays in a sector that was not open at any moment of the stay
        public int PiecesTooShort { get { return _piecesTooShort; } }   // open pieces shorter than MinTimeInSector

        public EntryCalculator(List<Configuration> configurations, List<ElementarySector> elementary,
                               List<Airspace> collapsed, OpeningScheme scheme)
        {
            _scheme = scheme;

            var elementaryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ElementarySector es in elementary) elementaryNames.Add(es.Name);
            var csById = new Dictionary<string, Airspace>(StringComparer.OrdinalIgnoreCase);
            foreach (Airspace cs in collapsed) csById[cs.Id] = cs;

            var accs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Configuration c in configurations)
            {
                accs.Add(c.AccName);

                var sectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ElementarySector es in c.Elementary) sectors.Add(es.Name);
                foreach (Airspace cs in c.Collapsed) sectors.Add(cs.Id);
                _configSectors[c.AccName + ";" + c.ConfigurationName] = sectors;

                foreach (string s in sectors)
                {
                    if (_members.ContainsKey(s))
                        continue;
                    var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Flatten(s, elementaryNames, csById, members, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    _members[s] = members;

                    foreach (string es in members)
                    {
                        if (!_sectorsOfElementary.TryGetValue(es, out List<string> list))
                        {
                            list = new List<string>();
                            _sectorsOfElementary[es] = list;
                        }
                        list.Add(s);
                    }
                }
            }

            // Open periods of every sector, walking each ACC's records in time order
            foreach (string acc in accs)
            {
                var last = new Dictionary<string, OpenPeriod>(StringComparer.OrdinalIgnoreCase); // last period of each sector in this ACC
                foreach (OpeningSchemeRecord r in scheme.GetRecords(acc))
                {
                    if (!_configSectors.TryGetValue(acc + ";" + r.ConfigurationName, out HashSet<string> open))
                        continue; // configuration not in the .cfg: nothing known to be open

                    foreach (string s in open)
                    {
                        if (last.TryGetValue(s, out OpenPeriod period) && r.Start <= period.End)
                        {
                            if (r.End > period.End) period.End = r.End; // still open: same period
                            continue;
                        }
                        period = new OpenPeriod { Acc = acc, Start = r.Start, End = r.End };
                        last[s] = period;
                        if (!_openPeriods.TryGetValue(s, out List<OpenPeriod> list))
                        {
                            list = new List<OpenPeriod>();
                            _openPeriods[s] = list;
                        }
                        list.Add(period);
                    }
                }
            }
        }

        // Elementary sectors of a sector, following nested collapsed sectors
        private static void Flatten(string name, HashSet<string> elementaryNames, Dictionary<string, Airspace> csById,
                                    HashSet<string> result, HashSet<string> visited)
        {
            if (!visited.Add(name))
                return;
            if (csById.TryGetValue(name, out Airspace cs))
            {
                foreach (Sector m in cs.Sectors)
                    Flatten(m.Name.Trim(), elementaryNames, csById, result, visited);
            }
            else if (elementaryNames.Contains(name))
                result.Add(name);
        }

        // LEVEL Elementary: inside = elementary sector -> intervals of this flight (from CrossingCalculator.Cross)
        public List<SectorEntry> ElementaryEntries(Trajectory trajectory, Dictionary<string, List<Interval>> inside, EntryParameters p)
        {
            var entries = new List<SectorEntry>();
            foreach (KeyValuePair<string, List<Interval>> kv in inside)
            {
                foreach (Interval stay in Interval.Merge(kv.Value, TimeSpan.FromSeconds(p.ReentryTime)))
                {
                    if (stay.Seconds < p.MinTimeInSector)
                        continue;
                    entries.Add(new SectorEntry
                    {
                        FlightId = trajectory.FlightIdentifier,
                        CallSign = trajectory.CallSign,
                        Level = EntryLevel.Elementary,
                        Cause = EntryCause.Crossing,
                        Sector = kv.Key,
                        Entry = stay.Start,
                        Exit = stay.End
                    });
                }
            }
            return entries;
        }

        // LEVEL Open: entries into the sectors the opening scheme has open
        public List<SectorEntry> OpenEntries(Trajectory trajectory, Dictionary<string, List<Interval>> inside, EntryParameters p)
        {
            var entries = new List<SectorEntry>();

            // Only the sectors that contain some elementary sector the flight entered
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string es in inside.Keys)
                if (_sectorsOfElementary.TryGetValue(es, out List<string> list))
                    candidates.UnionWith(list);

            foreach (string sector in candidates)
            {
                if (!_openPeriods.TryGetValue(sector, out List<OpenPeriod> periods))
                    continue; // never open in the opening scheme

                // Stays in the sector = union of its members' intervals (ES_a -> ES_b inside the same CS is one stay)
                var all = new List<Interval>();
                foreach (string es in _members[sector])
                    if (inside.TryGetValue(es, out List<Interval> iv))
                        all.AddRange(iv);
                List<Interval> stays = Interval.Merge(all, Interval.TOUCH);
                if (p.ReentryTime > 0)
                    stays = Interval.Merge(stays, TimeSpan.FromSeconds(p.ReentryTime));

                foreach (Interval stay in stays)
                {
                    bool open = false;
                    foreach (OpenPeriod period in periods)
                    {
                        // Part of the stay in which the sector is open
                        DateTime start = stay.Start > period.Start ? stay.Start : period.Start;
                        DateTime end = stay.End < period.End ? stay.End : period.End;
                        if (end <= start)
                            continue;
                        open = true;

                        if ((end - start).TotalSeconds < p.MinTimeInSector) { Interlocked.Increment(ref _piecesTooShort); continue; }

                        entries.Add(new SectorEntry
                        {
                            FlightId = trajectory.FlightIdentifier,
                            CallSign = trajectory.CallSign,
                            Level = EntryLevel.Open,
                            // Open when the flight crossed in -> real crossing; opened while the flight was inside -> Opened
                            Cause = period.Start <= stay.Start ? EntryCause.Crossing : EntryCause.Opened,
                            Sector = sector,
                            Acc = period.Acc,
                            Configuration = _scheme.GetConfigurationName(period.Acc, start),
                            Entry = start,
                            Exit = end
                        });
                    }
                    if (!open) Interlocked.Increment(ref _staysNeverOpen);
                }
            }

            return entries;
        }
    }
}
