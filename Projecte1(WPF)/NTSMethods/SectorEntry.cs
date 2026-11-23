using System;

namespace Projecte1_WPF_.NTS
{
    // Project parameters of the entry computation (seconds). Defaults = the Parameters panel defaults; with both at 0 the
    // open-sector entries total the 28,406 of the reference
    public class EntryParameters
    {
        // Minimum time a flight has to spend inside a sector for the entry to count
        public double MinTimeInSector { get; set; } = 30;

        // A flight that leaves a sector and enters it again after this time or more makes a new entry (reentry);
        // a shorter gap joins both stays into one entry
        public double ReentryTime { get; set; } = 60;
    }

    public enum EntryLevel
    {
        Elementary, // geometry only: entry into an elementary sector, whatever the opening scheme says
        Open        // entry into a sector (elementary or collapsed) that is open according to the opening scheme
    }

    public enum EntryCause
    {
        Crossing, // the flight crossed the boundary of the sector
        Opened    // the flight was already inside when the sector opened (Entry = opening time). Only for Level Open
    }

    // One entry of a flight into a sector
    public class SectorEntry
    {
        public long FlightId { get; set; }
        public string CallSign { get; set; }

        public EntryLevel Level { get; set; }
        public EntryCause Cause { get; set; }

        // Elementary sector name or collapsed sector id
        public string Sector { get; set; }

        // ACC and configuration in which the sector was open at Entry (null for Level Elementary)
        public string Acc { get; set; }
        public string Configuration { get; set; }

        public DateTime Entry { get; set; }
        public DateTime Exit { get; set; }
        public double DurationSeconds { get { return (Exit - Entry).TotalSeconds; } }
    }
}
