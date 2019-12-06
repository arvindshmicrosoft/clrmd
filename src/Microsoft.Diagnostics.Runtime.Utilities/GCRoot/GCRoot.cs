﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Runtime.Utilities
{
    /// <summary>
    /// A delegate for reporting GCRoot progress.
    /// </summary>
    /// <param name="source">The GCRoot sending the event.</param>
    /// <param name="current">The total number of objects processed.</param>
    public delegate void GCRootProgressEvent(GCRoot source, long current);

    /// <summary>
    /// A helper class to find the GC rooting chain for a particular object.
    /// </summary>
    public class GCRoot
    {
        private static readonly Stack<ClrObject> s_emptyStack = new Stack<ClrObject>();
        private int _maxTasks;

        /// <summary>
        /// Since GCRoot can be long running, this event will provide periodic updates to how many objects the algorithm
        /// has processed.  Note that in the case where we search all objects and do not find a path, it's unlikely that
        /// the number of objects processed will ever reach the total number of objects on the heap.  That's because there
        /// will be garbage objects on the heap we can't reach.
        /// </summary>
        public event GCRootProgressEvent ProgressUpdate;

        /// <summary>
        /// Returns the heap that's associated with this GCRoot instance.
        /// </summary>
        public ClrHeap Heap { get; }

        /// <summary>
        /// Whether or not to allow GC root to search in parallel or not.  Note that GCRoot does not have to respect this
        /// flag.  Parallel searching of roots will only happen if a copy of the stack and heap were built using BuildCache,
        /// and if the entire heap was cached.  Note that ClrMD and underlying APIs do NOT support multithreading, so this
        /// is only used when we can ensure all relevant data is local memory and we do not need to touch the debuggee.
        /// </summary>
        public bool AllowParallelSearch { get; set; } = false;

        /// <summary>
        /// The maximum number of tasks allowed to run in parallel, if GCRoot does a parallel search.
        /// </summary>
        public int MaximumTasksAllowed
        {
            get => _maxTasks;
            set
            {
                if (_maxTasks < 0)
                    throw new InvalidOperationException($"{nameof(MaximumTasksAllowed)} cannot be less than 0!");

                _maxTasks = value;
            }
        }

        /// <summary>
        /// Creates a GCRoot helper object for the given heap.
        /// </summary>
        /// <param name="heap">The heap the object in question is on.</param>
        public GCRoot(ClrHeap heap)
        {
            Heap = heap ?? throw new ArgumentNullException(nameof(heap));
            _maxTasks = Environment.ProcessorCount * 2;
        }

        /// <summary>
        /// Enumerates GCRoots of a given object.  Similar to !gcroot.  Note this function only returns paths that are fully unique.
        /// </summary>
        /// <param name="target">The target object to search for GC rooting.</param>
        /// <param name="cancelToken">A cancellation token to stop enumeration.</param>
        /// <returns>An enumeration of all GC roots found for target.</returns>
        public IEnumerable<GCRootPath> EnumerateGCRoots(ulong target, CancellationToken cancelToken)
        {
            return EnumerateGCRoots(target, true, cancelToken);
        }

        /// <summary>
        /// Enumerates GCRoots of a given object.  Similar to !gcroot.
        /// </summary>
        /// <param name="target">The target object to search for GC rooting.</param>
        /// <param name="unique">Whether to only return fully unique paths.</param>
        /// <param name="cancelToken">A cancellation token to stop enumeration.</param>
        /// <returns>An enumeration of all GC roots found for target.</returns>
        public IEnumerable<GCRootPath> EnumerateGCRoots(ulong target, bool unique, CancellationToken cancelToken)
        {
            long lastObjectReported = 0;

            bool parallel = AllowParallelSearch && _maxTasks > 0;

            Dictionary<ulong, LinkedListNode<ClrObject>> knownEndPoints = new Dictionary<ulong, LinkedListNode<ClrObject>>()
            {
                { target, new LinkedListNode<ClrObject>(Heap.GetObject(target)) }
            };

            ObjectSet processedObjects = parallel
                ? new ParallelObjectSet(Heap)
                : new ObjectSet(Heap);

            Task<Tuple<LinkedList<ClrObject>, IClrRoot>>[] tasks = parallel
                ? new Task<Tuple<LinkedList<ClrObject>, IClrRoot>>[_maxTasks]
                : null;

            int initial = 0;

            foreach (IClrRoot root in Heap.EnumerateRoots())
            {
                GCRootPath? gcRootPath = ProcessRoot(root);
                if (gcRootPath.HasValue)
                    yield return gcRootPath.Value;
            }

            if (parallel)
            {
                foreach (Tuple<LinkedList<ClrObject>, IClrRoot> result in WhenEach(tasks))
                {
                    ReportObjectCount(processedObjects.Count);
                    yield return new GCRootPath { Root = result.Item2, Path = result.Item1.ToArray() };
                }
            }

            yield break;

            GCRootPath? ProcessRoot(IClrRoot root)
            {
                var rootObject = root.Object;
                GCRootPath? result = null;

                if (parallel)
                {
                    Task<Tuple<LinkedList<ClrObject>, IClrRoot>> task = Task.Run(
                        () =>
                            {
                                LinkedList<ClrObject> path = PathsTo(processedObjects, knownEndPoints, rootObject, target, unique, cancelToken).FirstOrDefault();
                                return new Tuple<LinkedList<ClrObject>, IClrRoot>(path, path == null ? null : root);
                            },
                        cancelToken);

                    if (initial < tasks.Length)
                    {
                        tasks[initial++] = task;
                    }
                    else
                    {
                        int i = Task.WaitAny(tasks);
                        Task<Tuple<LinkedList<ClrObject>, IClrRoot>> completed = tasks[i];
                        tasks[i] = task;

                        if (completed.Result.Item1 != null)
                            result = new GCRootPath { Root = completed.Result.Item2, Path = completed.Result.Item1.ToArray() };
                    }
                }
                else
                {
                    LinkedList<ClrObject> path = PathsTo(processedObjects, knownEndPoints, rootObject, target, unique, cancelToken).FirstOrDefault();
                    if (path != null)
                        result = new GCRootPath { Root = root, Path = path.ToArray() };
                }

                ReportObjectCount(processedObjects.Count);

                return result;
            }

            void ReportObjectCount(long curr)
            {
                if (curr != lastObjectReported)
                {
                    lastObjectReported = curr;
                    ProgressUpdate?.Invoke(this, lastObjectReported);
                }
            }
        }

        private static IEnumerable<Tuple<LinkedList<ClrObject>, IClrRoot>> WhenEach(Task<Tuple<LinkedList<ClrObject>, IClrRoot>>[] tasks)
        {
            List<Task<Tuple<LinkedList<ClrObject>, IClrRoot>>> taskList = tasks.Where(t => t != null).ToList();

            while (taskList.Count > 0)
            {
                Task<Tuple<LinkedList<ClrObject>, IClrRoot>> task = Task.WhenAny(taskList).Result;
                if (task.Result.Item1 != null)
                    yield return task.Result;

                bool removed = taskList.Remove(task);
                Debug.Assert(removed);
            }
        }

        /// <summary>
        /// Returns the path from the start object to the end object (or null if no such path exists).
        /// </summary>
        /// <param name="source">The initial object to start the search from.</param>
        /// <param name="target">The object we are searching for.</param>
        /// <param name="cancelToken">A cancellation token to stop searching.</param>
        /// <returns>A path from 'source' to 'target' if one exists, null if one does not.</returns>
        public LinkedList<ClrObject> FindSinglePath(ulong source, ulong target, CancellationToken cancelToken)
        {
            return PathsTo(new ObjectSet(Heap), null, new ClrObject(source, Heap.GetObjectType(source)), target, false, cancelToken).FirstOrDefault();
        }

        /// <summary>
        /// Returns the path from the start object to the end object (or null if no such path exists).
        /// </summary>
        /// <param name="source">The initial object to start the search from.</param>
        /// <param name="target">The object we are searching for.</param>
        /// <param name="unique">Whether to only enumerate fully unique paths.</param>
        /// <param name="cancelToken">A cancellation token to stop enumeration.</param>
        /// <returns>A path from 'source' to 'target' if one exists, null if one does not.</returns>
        public IEnumerable<LinkedList<ClrObject>> EnumerateAllPaths(ulong source, ulong target, bool unique, CancellationToken cancelToken)
        {
            return PathsTo(
                new ObjectSet(Heap),
                new Dictionary<ulong, LinkedListNode<ClrObject>>(),
                new ClrObject(source, Heap.GetObjectType(source)),
                target,
                unique,
                cancelToken);
        }

        private IEnumerable<LinkedList<ClrObject>> PathsTo(
            ObjectSet seen,
            Dictionary<ulong, LinkedListNode<ClrObject>> knownEndPoints,
            ClrObject source,
            ulong target,
            bool unique,
            CancellationToken cancelToken)
        {
            LinkedList<PathEntry> path = new LinkedList<PathEntry>();

            if (knownEndPoints != null && knownEndPoints.TryGetValue(source.Address, out LinkedListNode<ClrObject> ending))
            {
                yield return GetResult(ending);
                yield break;
            }

            if (!seen.Add(source.Address))
                yield return null;

            if (source.Type is null)
                yield break;

            if (source.Address == target)
            {
                path.AddLast(new PathEntry { Object = source });
                yield return GetResult();

                yield break;
            }

            path.AddLast(
                new PathEntry
                {
                    Object = source,
                    Todo = GetRefs(source, out bool foundTarget, out LinkedListNode<ClrObject> foundEnding)
                });

            // Did the 'start' object point directly to 'end'?  If so, early out.
            if (foundTarget)
            {
                path.AddLast(new PathEntry { Object = Heap.GetObject(target) });
                yield return GetResult();
            }
            else if (foundEnding != null)
            {
                yield return GetResult(foundEnding);
            }

            while (path.Count > 0)
            {
                cancelToken.ThrowIfCancellationRequested();

                TraceFullPath(null, path);
                PathEntry last = path.Last.Value;

                if (last.Todo.Count == 0)
                {
                    // We've exhausted all children and didn't find the target.  Remove this node
                    // and continue.
                    path.RemoveLast();
                }
                else
                {
                    // We loop here in case we encounter an object we've already processed (or if
                    // we can't get an object's type...inconsistent heap happens sometimes).
                    do
                    {
                        cancelToken.ThrowIfCancellationRequested();
                        ClrObject next = last.Todo.Pop();

                        // Now that we are in the process of adding 'next' to the path, don't ever consider
                        // this object in the future.
                        if (!seen.Add(next.Address))
                            continue;

                        // We should never reach the 'end' here, as we always check if we found the target
                        // value when adding refs below.
                        Debug.Assert(next.Address != target);

                        PathEntry nextPathEntry = new PathEntry
                        {
                            Object = next,
                            Todo = GetRefs(next, out foundTarget, out foundEnding)
                        };

                        path.AddLast(nextPathEntry);

                        // If we found the target object while enumerating refs of the current object, we are done.
                        if (foundTarget)
                        {
                            path.AddLast(new PathEntry { Object = Heap.GetObject(target) });
                            TraceFullPath("FoundTarget", path);

                            yield return GetResult();

                            path.RemoveLast();
                            path.RemoveLast();
                        }
                        else if (foundEnding != null)
                        {
                            TraceFullPath(path, foundEnding);
                            yield return GetResult(foundEnding);

                            path.RemoveLast();
                        }

                        // Now that we've added a new entry to 'path', break out of the do/while that's looping through Todo.
                        break;
                    } while (last.Todo.Count > 0);
                }
            }

            Stack<ClrObject> GetRefs(
                ClrObject obj,
                out bool found,
                out LinkedListNode<ClrObject> end)
            {
                // These asserts slow debug down by a lot, but it's important to ensure consistency in retail.
                //Debug.Assert(obj.Type != null);
                //Debug.Assert(obj.Type == _heap.GetObjectType(obj.Address));

                Stack<ClrObject> result = null;

                found = false;
                end = null;
                if (obj.Type.ContainsPointers || obj.Type.IsCollectible)
                {
                    foreach (ClrObject reference in obj.EnumerateReferences(true))
                    {
                        cancelToken.ThrowIfCancellationRequested();
                        if (!unique && end == null && knownEndPoints != null)
                        {
                            lock (knownEndPoints)
                            {
                                knownEndPoints.TryGetValue(reference.Address, out end);
                            }
                        }

                        if (reference.Address == target)
                        {
                            found = true;
                        }

                        if (!seen.Contains(reference.Address))
                        {
                            result ??= new Stack<ClrObject>();
                            result.Push(reference);
                        }
                    }
                }

                return result ?? s_emptyStack;
            }

            LinkedList<ClrObject> GetResult(LinkedListNode<ClrObject> end = null)
            {
                LinkedList<ClrObject> result = new LinkedList<ClrObject>(path.Select(p => p.Object));

                for (; end != null; end = end.Next)
                    result.AddLast(end.Value);

                if (!unique && knownEndPoints != null)
                    lock (knownEndPoints)
                        for (LinkedListNode<ClrObject> node = result.First; node != null; node = node.Next)
                        {
                            ulong address = node.Value.Address;
                            if (knownEndPoints.ContainsKey(address))
                                break;

                            knownEndPoints[address] = node;
                        }

                return result;
            }
        }

        internal static bool IsTooLarge(ulong obj, ClrType type, ClrSegment seg)
        {
            ulong size = type.Heap.GetObjectSize(obj, type);
            if (!seg.IsLargeObjectSegment && size >= 85000)
                return true;

            return obj + size > seg.End;
        }

        [Conditional("GCROOTTRACE")]
        private static void TraceFullPath(LinkedList<PathEntry> path, LinkedListNode<ClrObject> foundEnding)
        {
            Debug.WriteLine($"FoundEnding: {string.Join(" ", path.Select(p => p.Object.ToString()))} {string.Join(" ", NodeToList(foundEnding))}");
        }

        private static List<string> NodeToList(LinkedListNode<ClrObject> tmp)
        {
            List<string> list = new List<string>();
            for (; tmp != null; tmp = tmp.Next)
                list.Add(tmp.Value.ToString());

            return list;
        }

        [Conditional("GCROOTTRACE")]
        private static void TraceFullPath(string prefix, LinkedList<PathEntry> path)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                prefix += ": ";
            else
                prefix = string.Empty;

            Debug.WriteLine(prefix + string.Join(" ", path.Select(p => p.Object.ToString())));
        }

        private struct PathEntry
        {
            public ClrObject Object;
            public Stack<ClrObject> Todo;

            public override string ToString()
            {
                return Object.ToString();
            }
        }
    }
}