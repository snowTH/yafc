using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using YAFC.UI;

namespace YAFC.Model
{
    public class UndoSystem
    {
        public uint version { get; private set; }
        private readonly List<UndoSnapshot> currentUndoBatch = new List<UndoSnapshot>();
        private readonly List<ModelObject> changedList = new List<ModelObject>();
        private readonly Stack<UndoBatch> undo = new Stack<UndoBatch>();
        private readonly Stack<UndoBatch> redo = new Stack<UndoBatch>();
        internal void RecordChange(ModelObject target, bool visualOnly)
        {
            if (changedList.Count == 0)
            {
                version++;
                Ui.DispatchInMainThread(MakeUndoBatch, this);
            }
            
            if (target.objectVersion == version)
                return;

            changedList.Add(target);
            target.objectVersion = version;
            if (visualOnly && undo.Count > 0 && undo.Peek().Contains(target))
                return;
            
            var builder = target.GetUndoBuilder();
            currentUndoBatch.Add(builder.MakeUndoSnapshot(target));
        }

        private static readonly SendOrPostCallback MakeUndoBatch = delegate(object state)
        {
            var system = state as UndoSystem;
            for (var i = 0; i < system.changedList.Count; i++)
                system.changedList[i].ThisChanged();
            system.changedList.Clear();
            if (system.currentUndoBatch.Count == 0)
                return;
            var batch = new UndoBatch(system.currentUndoBatch.ToArray());
            system.undo.Push(batch);
            system.redo.Clear();
            system.currentUndoBatch.Clear();
        };

        public void PerformUndo()
        {
            if (undo.Count == 0)
                return;
            redo.Push(undo.Pop().Restore(++version));
        }

        public void PerformRedo()
        {
            if (redo.Count == 0)
                return;
            undo.Push(redo.Pop().Restore(++version));
        }
    }
    internal readonly struct UndoSnapshot
    {
        internal readonly ModelObject target;
        internal readonly object[] managedReferences;
        internal readonly byte[] unmanagedData;

        public UndoSnapshot(ModelObject target, object[] managed, byte[] unmanaged)
        {
            this.target = target;
            managedReferences = managed;
            unmanagedData = unmanaged;
        }

        public UndoSnapshot Restore()
        {
            var builder = target.GetUndoBuilder();
            var redo = builder.MakeUndoSnapshot(target);
            builder.RevertToUndoSnapshot(target, this);
            return redo;
        }
    }

    internal readonly struct UndoBatch
    {
        public readonly UndoSnapshot[] snapshots;
        public UndoBatch(UndoSnapshot[] snapshots)
        {
            this.snapshots = snapshots;
        }
        public UndoBatch Restore(uint undoState)
        {
            for (var i = 0; i < snapshots.Length; i++)
            {
                snapshots[i] = snapshots[i].Restore();
                snapshots[i].target.objectVersion = undoState;
            }
            foreach (var snapshot in snapshots)
                snapshot.target.AfterDeserialize();
            foreach (var snapshot in snapshots)
                snapshot.target.ThisChanged(); 
            return this;
        }
        
        public bool Contains(ModelObject target)
        {
            foreach (var snapshot in snapshots)
            {
                if (snapshot.target == target)
                    return true;
            }
            return false;
        }
    }

    public class UndoSnapshotBuilder
    {
        private readonly MemoryStream stream = new MemoryStream();
        private readonly List<object> managedRefs = new List<object>();
        public readonly BinaryWriter writer;
        private ModelObject currentTarget;

        internal UndoSnapshotBuilder()
        {
            writer = new BinaryWriter(stream);
        }

        internal void BeginBuilding(ModelObject target)
        {
            currentTarget = target;
        }

        internal UndoSnapshot Build()
        {
            byte[] buffer = null;
            if (stream.Position > 0)
            {
                buffer = new byte[stream.Position];
                Array.Copy(stream.GetBuffer(), buffer, stream.Position);
            }
            var result = new UndoSnapshot(currentTarget, managedRefs.Count > 0 ? managedRefs.ToArray() : null, buffer);
            stream.Position = 0;
            managedRefs.Clear();
            currentTarget = null;
            return result;
        }

        public void WriteManagedReference(object reference) => managedRefs.Add(reference);
        public void WriteManagedReferences(IEnumerable<object> references) => managedRefs.AddRange(references);
    }

    public class UndoSnapshotReader
    {
        public BinaryReader reader { get; private set; }
        private int refId;
        private object[] managed;
        
        internal UndoSnapshotReader() {}

        public object ReadManagedReference() => managed[refId++];

        internal void DoSnapshot(UndoSnapshot snapshot)
        {
            if (snapshot.unmanagedData != null)
            {
                var stream = new MemoryStream(snapshot.unmanagedData, false);
                reader = new BinaryReader(stream);
            }
            else reader = null;
            managed = snapshot.managedReferences;
            refId = 0;
        }
    }
}