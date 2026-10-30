using System;
using System.Collections.Generic;

using CloudberryKingdom.Levels;

namespace CloudberryKingdom
{
    /// <summary>
    /// Platform items (the cloudberry substrate's R3/R8): the same level is generated whether or not an item is held;
    /// a platform of an unheld item is drawn faint and is not landable (no collision, no interaction), as GhostBlock's
    /// own phase-out (Core.Active) already does in the engine. Off unless Enabled: the game is unchanged by default.
    ///
    /// Which objects an item gates is decided once, at generation (MakeLevel → TagNewObjects): an object of the item's
    /// kinds that a level PIECE made is gated; the ladders that join pieces (elevators, MovingPlatform) and every runtime
    /// object are not. An emitted elevator platform inherits its emitter's gate (BlockEmitter.Emit).
    /// </summary>
    public static class PlatformSuppression
    {
        /// <summary>
        /// The items, named by the Upgrade that makes them, and the object kinds each one gates.
        /// The kinds were measured per upgrade (one-upgrade levels, P0 census; see GeneratorApi's "kinds" table).
        /// </summary>
        public static readonly Upgrade[] Items =
        {
            Upgrade.MovingBlock, Upgrade.FallingBlock, Upgrade.BouncyBlock, Upgrade.GhostBlock, Upgrade.Cloud,
            Upgrade.Elevator, Upgrade.Pendulum, Upgrade.Conveyor, Upgrade.FlyBlob,
        };

        static readonly Dictionary<ObjectType, int> ItemOfKind = new Dictionary<ObjectType, int>
        {
            { ObjectType.MovingBlock, 0 },
            { ObjectType.FallingBlock, 1 },
            { ObjectType.BouncyBlock, 2 },
            { ObjectType.GhostBlock, 3 },
            { ObjectType.Cloud, 4 },
            { ObjectType.BlockEmitter, 5 }, { ObjectType.MovingPlatform, 5 },
            { ObjectType.Pendulum, 6 },
            { ObjectType.ConveyorBlock, 7 },
            { ObjectType.FlyingBlob, 8 },
        };

        /// <summary>The object kinds an item gates (for reports).</summary>
        public static List<ObjectType> KindsOf(int item)
        {
            var list = new List<ObjectType>();
            foreach (var kv in ItemOfKind) if (kv.Value == item) list.Add(kv.Key);
            return list;
        }

        /// <summary>The item index for an item name (the Upgrade's name, case-insensitive), or -1.</summary>
        public static int ItemIndex(string name)
        {
            for (int i = 0; i < Items.Length; i++)
                if (string.Equals(Items[i].ToString(), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Opacity scale for an unheld item's drawing.</summary>
        public static float DrawAlpha = .25f;

        /// <summary>True when held-ness is in force; false = everything solid (the game as it always was).</summary>
        public static bool Enabled;

        static readonly bool[] Held = new bool[Items.Length];

        /// <summary>Enable suppression with exactly these items held (names as Items).</summary>
        public static void SetHeld(IEnumerable<string> held)
        {
            Array.Clear(Held, 0, Held.Length);
            foreach (string name in held)
            {
                int i = ItemIndex(name);
                if (i < 0) throw new ArgumentException("unknown platform item '" + name + "' (items: " + string.Join(", ", Items) + ")");
                Held[i] = true;
            }
            Enabled = true;
        }

        public static void Disable()
        {
            Enabled = false;
        }

        public static IEnumerable<string> HeldNames()
        {
            for (int i = 0; i < Items.Length; i++)
                if (Held[i]) yield return Items[i].ToString();
        }

        public static bool IsSuppressed(ObjectData core)
        {
            return Enabled && core.GateItem >= 0 && !Held[core.GateItem];
        }

        static int TagEpoch;
        public static int NewTagEpoch() { return ++TagEpoch; }

        /// <summary>Tags the level's objects not yet tagged in this MakeLevel pass with the piece that made them.</summary>
        public static void TagNewObjects(Level level, int epoch, int seedPiece, PieceSeedData piece)
        {
            bool gates = piece.Ladder == Level.LadderType.None;
            foreach (ObjectBase obj in level.Blocks) Tag(obj, epoch, seedPiece, gates);
            foreach (ObjectBase obj in level.Objects) Tag(obj, epoch, seedPiece, gates);
        }

        static void Tag(ObjectBase obj, int epoch, int seedPiece, bool gates)
        {
            if (obj == null || obj.Core.GenTagEpoch == epoch) return;
            obj.Core.GenTagEpoch = epoch;
            obj.Core.GenSeedPiece = seedPiece;
            int item;
            obj.Core.GateItem = gates && ItemOfKind.TryGetValue(obj.Core.MyType, out item) ? item : -1;
        }
    }
}
