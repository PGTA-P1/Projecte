using System;
using System.Collections.Generic;
using System.Diagnostics;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.NTS
{
    // Same idea as AirblockMerger, one level up:
    //   AirblockMerger:         airblocks (.are) of an elementary sector (.sls)  -> outline
    //   CollapsedSectorMerger:  elementary sectors of a collapsed sector (.spc)  -> outline
    // The elementary sectors are built from their SectorVolume list (airblock + FL band), then fused by band.
    // Outline of one collapsed sector (.spc) for one level band
    public class MergedCollapsedSector : IMergedSector
    {
        public string Name { get; set; }
        public int BottomLevel { get; set; }
        public int TopLevel { get; set; }
        public List<List<(double Latitude, double Longitude)>> OuterRings { get; } =
            new List<List<(double Latitude, double Longitude)>>();
    }

    public static class CollapsedSectorMerger
    {
        public static List<MergedCollapsedSector> Merge(Airspace collapsed, List<ElementarySector> elementary,
                                                  List<Airblock> airblocks,
                                                  double tolerance = AirblockMerger.DEFAULT_SIMPLIFY_DEG)
        {
            // Exact band shapes of every member elementary sector
            var all = new List<BandGeometry>();
            for (int i = 0; i < collapsed.Sectors.Count; i++)
            {
                string memberName = collapsed.Sectors[i].Name;
                ElementarySector es = FindElementary(elementary, memberName);
                if (es == null)
                {
                    Debug.WriteLine($"[CollapsedSectorMerger] '{collapsed.Id}': elementary sector '{memberName}' not found.");
                    continue;
                }
                all.AddRange(AirblockMerger.MergeGeometries(es, airblocks));
            }

            // Union of the shapes that share the same (BottomLevel, TopLevel)
            var result = new List<MergedCollapsedSector>();
            bool[] used = new bool[all.Count];

            for (int i = 0; i < all.Count; i++)
            {
                if (used[i])
                    continue;

                var group = new List<Geometry>();
                for (int j = i; j < all.Count; j++)
                {
                    if (used[j] || all[j].BottomLevel != all[i].BottomLevel || all[j].TopLevel != all[i].TopLevel)
                        continue;
                    used[j] = true;
                    group.Add(all[j].Shape);
                }

                Geometry merged = UnaryUnionOp.Union(group);
                MergedAirblocks fused = AirblockMerger.ToMerged(collapsed.Id, all[i].BottomLevel, all[i].TopLevel,
                                                                merged, tolerance);
                if (fused == null)
                    continue;

                var outline = new MergedCollapsedSector
                {
                    Name = fused.Name,
                    BottomLevel = fused.BottomLevel,
                    TopLevel = fused.TopLevel
                };
                outline.OuterRings.AddRange(fused.OuterRings);
                result.Add(outline);
            }

            return result;
        }

        private static ElementarySector FindElementary(List<ElementarySector> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return list[i];
            }
            return null;
        }
    }
}