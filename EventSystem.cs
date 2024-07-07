using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

public abstract partial class EventSystem<T> : SystemBase where T : unmanaged
{
    // Debug settings that might be usefull in buillds
    static readonly bool SuppressEventErrors = UnityEngine.Application.isEditor == false;
    static readonly bool TrackEvents = UnityEngine.Application.isEditor;

    public delegate void ProcessQueueFunc(NativeQueue<T> queue);
    public delegate void ProcessListFunc(NativeList<T> list);

    public delegate void ProcessQueueCmdFunc(ref EntityCommandBuffer cmd, NativeQueue<T> queue);
    public delegate void ProcessListCmdFunc(ref EntityCommandBuffer cmd, NativeList<T> list);

    public virtual int EventSlotCapacity => 64;

    public struct EventSlots
    {
        public ushort StartIndex;
        public ushort Index;
        public ushort Slots;

        public UnsafeBitArray UsedMask;
        public UnsafeList<T> EventList;

        public bool Full { get => (Index - StartIndex) >= Slots; }
        public void Set(T data)
        {
            if ((Index - StartIndex) >= Slots)
            {
#if UNITY_EDITOR
                throw new Exception("Used all slots in EventlSlot");
#else
                return;
#endif
            }

            UsedMask.Set(Index, true);
            EventList[Index] = data;
            Index++;
        }
    }

    protected UnsafeList<T> _EventList;
    protected UnsafeBitArray _UsedMask;

    protected List<NativeQueue<T>> _EventQueues = new List<NativeQueue<T>>();

    protected JobHandle _ProducerHandle;

#if UNITY_EDITOR
    protected List<string> _Allocations = new List<string>();
#endif

    protected void CreateEvents()
    {
        _EventList = new UnsafeList<T>(EventSlotCapacity, Allocator.Persistent);
        _UsedMask = new UnsafeBitArray(EventSlotCapacity, Allocator.Persistent);
        _UsedMask.Resize(EventSlotCapacity);
    }

    protected void DisposeEvents()
    {
        _EventList.Dispose();
        _UsedMask.Dispose();
    }

    public virtual NativeQueue<T> GetEventQueue()
    {
        var queue = new NativeQueue<T>(Allocator.TempJob);
        _EventQueues.Add(queue);

        return queue;
    }
    public virtual NativeQueue<T>.ParallelWriter GetEventQueuePW()
    {
        var queue = new NativeQueue<T>(Allocator.TempJob);
        _EventQueues.Add(queue);

        return queue.AsParallelWriter();
    }

    public virtual EventSlots GetEventSlots(ushort slots = 8)
    {
        var valid = VerifyBuffer(slots);
        var index = _EventList.Length;

        if (valid)
            _EventList.Length = _EventList.Length + slots;
        else
            index = (ushort)(_EventList.Length - slots);

        return new EventSlots
        {
            StartIndex = (ushort)index,
            Index = (ushort)index,
            Slots = slots,
            UsedMask = _UsedMask,
            EventList = _EventList,
        };
    }

    public void QueueEvent(T data)
    {
        if (VerifyBuffer() == false)
            return;

        _UsedMask.Set(_EventList.Length, true);
        _EventList.Add(data);
    }

    private bool VerifyBuffer(int slots = 0)
    {
        if ((_EventList.Length + slots) >= EventSlotCapacity)
        {
            if (SuppressEventErrors)
                return false;

#if UNITY_EDITOR
            if (TrackEvents)
            {
                var str = "Allocations made from\n\n";
                for (int i = 0; i < _Allocations.Count; i++)
                {
                    str += "Stack Trace:\n" + _Allocations[i] + "\n\n";
                }
                UnityEngine.Debug.LogError(str);
                throw new Exception($"In {this} To many event slots used!");
            }
            else
            {
                throw new Exception($"To many event slots used!");
            }
#else
            return false;
#endif
        }

#if UNITY_EDITOR
        if (TrackEvents)
        {
            _Allocations.Add(Environment.StackTrace);
        }
#endif

        return true;
    }

    public virtual void Clear()
    {
        _ProducerHandle.Complete();
        _ProducerHandle = default;

        _UsedMask.Clear();
        _EventList.Clear();

        for (int i = 0; i < _EventQueues.Count; i++)
        {
            if (_EventQueues[i].IsCreated)
                _EventQueues[i].Dispose();
        }

        _EventQueues.Clear();
        ClearAllocatorTracker();
    }

    protected void ClearAllocatorTracker()
    {
#if UNITY_EDITOR
        _Allocations.Clear();
#endif
    }

    protected override void OnStopRunning()
    {
        Clear();
    }

    public void AddJobHandleForProducer(JobHandle dependency)
    {
        _ProducerHandle = JobHandle.CombineDependencies(_ProducerHandle, dependency);
    }

    protected void ProcessEvents(ProcessQueueFunc processQueue, ProcessListFunc processList)
    {
        _ProducerHandle.Complete();
        _ProducerHandle = default;

        for (int i = 0; i < _EventQueues.Count; i++)
        {
            if (_EventQueues[i].IsCreated)
            {
                if (_EventQueues[i].Count > 0)
                    processQueue(_EventQueues[i]);

                _EventQueues[i].Dispose();
            }
        }
        _EventQueues.Clear();

        if (_UsedMask.TestAny(0, _UsedMask.Length))
        {
            var sortedList = new NativeList<T>(_EventList.Length, Allocator.TempJob);
            for (int i = 0; i < _EventList.Length; i++)
            {
                if (_UsedMask.IsSet(i))
                    sortedList.Add(_EventList[i]);
            }

            if (sortedList.Length > 0)
                processList(sortedList);

            sortedList.Dispose();
        }

        _EventList.Clear();

        _UsedMask.Clear();

        ClearAllocatorTracker();
    }

    protected void ProcessEvents(ProcessQueueCmdFunc processQueue, ProcessListCmdFunc processList)
    {
        var cmd = new EntityCommandBuffer(Allocator.TempJob);

        ProcessEvents(ref cmd, processQueue, processList);

        cmd.Playback(EntityManager);
        cmd.Dispose();
    }

    protected void ProcessEvents(ref EntityCommandBuffer cmd, ProcessQueueCmdFunc processQueue, ProcessListCmdFunc processList)
    {
        _ProducerHandle.Complete();
        _ProducerHandle = default;

        for (int i = 0; i < _EventQueues.Count; i++)
        {
            if (_EventQueues[i].IsCreated)
            {
                if (_EventQueues[i].Count > 0)
                    processQueue(ref cmd, _EventQueues[i]);
                else
                    _EventQueues[i].Dispose();
            }
        }
        _EventQueues.Clear();

        if (_EventList.IsCreated)
        {
            if (_UsedMask.TestAny(0, _UsedMask.Length))
            {
                var sortedList = new NativeList<T>(_EventList.Length, Allocator.TempJob);
                for (int i = 0; i < _EventList.Length; i++)
                {
                    if (_UsedMask.IsSet(i))
                        sortedList.Add(_EventList[i]);
                }

                if (sortedList.Length > 0)
                    processList(ref cmd, sortedList);
            }

            _EventList.Clear();
            _UsedMask.Clear();
        }

        ClearAllocatorTracker();
    }

    public NativeList<T> AccumulateEvents()
    {
        _ProducerHandle.Complete();
        _ProducerHandle = default;

        var sortedList = new NativeList<T>(_EventList.Length, WorldUpdateAllocator);
        if (_EventList.IsCreated)
        {
            if (_UsedMask.TestAny(0, _UsedMask.Length))
            {
                for (int i = 0; i < _EventList.Length; i++)
                {
                    if (_UsedMask.IsSet(i))
                        sortedList.Add(_EventList[i]);
                }
            }

            _EventList.Clear();
            _UsedMask.Clear();
        }

        for (int i = 0; i < _EventQueues.Count; i++)
        {
            if (_EventQueues[i].Count > 0)
                sortedList.AddRange(_EventQueues[i].ToArray(WorldUpdateAllocator));

            _EventQueues[i].Dispose();
        }
        _EventQueues.Clear();

        ClearAllocatorTracker();

        return sortedList;
    }
}
