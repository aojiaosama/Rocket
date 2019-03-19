﻿using Rocket.API;
using Rocket.API.DependencyInjection;
using Rocket.API.Eventing;
using Rocket.API.Logging;
using Rocket.API.Scheduling;
using Rocket.Core.Logging;
using Rocket.Core.Scheduling;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Rocket.Console.Scheduling
{
    public class SimpleTaskScheduler : ITaskScheduler, IDisposable
    {
        protected IDependencyContainer Container { get; set; }
        protected List<SimpleTask> InternalTasks { get; set; }

        private volatile int taskIds;
        private readonly AsyncThreadPool asyncThreadPool;

        public SimpleTaskScheduler(IDependencyContainer container)
        {
            Container = container;
            MainThread = Thread.CurrentThread;

            asyncThreadPool = new AsyncThreadPool(this);
            asyncThreadPool.Start();
            InternalTasks = new List<SimpleTask>();
        }

        public Thread MainThread { get; }

        public IEnumerable<IScheduledTask> Tasks =>
            InternalTasks.Where(c => c.IsReferenceAlive && c.Owner.IsAlive);

        public IScheduledTask ScheduleUpdate(ILifecycleObject @object, Action action, string taskName, ExecutionTargetContext target)
        {
            if (!@object.IsAlive)
                return null;

            SimpleTask task = new SimpleTask(++taskIds, taskName, this, @object, action, target);

            TriggerEvent(task, async (sender, @event) =>
            {
                if (target != ExecutionTargetContext.Sync && @object.IsAlive) return;

                if (@event != null && ((ICancellableEvent)@event).IsCancelled) return;

                action();
                InternalTasks.Remove(task);
            });

            return task;
        }

        public IScheduledTask ScheduleAt(ILifecycleObject @object, Action action, string taskName, DateTime date, bool runAsync = false)
        {
            if (!@object.IsAlive)
                return null;

            SimpleTask task = new SimpleTask(++taskIds, taskName, this, @object, action,
                runAsync ? ExecutionTargetContext.Async : ExecutionTargetContext.Sync)
            {
                StartTime = date
            };
            TriggerEvent(task);
            return task;
        }

        public IScheduledTask SchedulePeriodically(ILifecycleObject @object, Action action, string taskName, TimeSpan period, TimeSpan? delay = null,
                                          bool runAsync = false)
        {
            if (!@object.IsAlive)
                return null;

            SimpleTask task = new SimpleTask(++taskIds, taskName, this, @object, action,
                runAsync ? ExecutionTargetContext.Async : ExecutionTargetContext.Sync)
            {
                Period = period
            };

            if (delay != null)
                task.StartTime = DateTime.UtcNow + delay;

            TriggerEvent(task);
            return task;
        }

        public virtual bool CancelTask(IScheduledTask task)
        {
            if (task.IsFinished || task.IsCancelled)
                return false;

            ((SimpleTask)task).IsCancelled = true;
            return true;
        }

        protected virtual void TriggerEvent(SimpleTask task, EventCallback cb = null)
        {
            asyncThreadPool.EventWaitHandle.Set();

            TaskScheduleEvent e = new TaskScheduleEvent(task);
            if (!(task.Owner is IEventEmitter owner))
            {
                return;
            }

            IEventBus eventBus = Container.Resolve<IEventBus>();
            if (eventBus == null)
            {
                InternalTasks.Add(task);
                cb?.Invoke(owner, null);
                return;
            }

            eventBus.Emit(owner, e, async @event =>
            {
                task.IsCancelled = e.IsCancelled;

                if (!e.IsCancelled)
                    InternalTasks.Add(task);

                cb?.Invoke(owner, @event);
            });
        }

        protected internal virtual void RunTask(IScheduledTask t)
        {
            var task = (SimpleTask)t;
            if (!task.IsReferenceAlive)
            {
                InternalTasks.Remove(task);
                return;
            }

            if (!t.Owner.IsAlive)
                return;

            if (task.StartTime != null && task.StartTime > DateTime.UtcNow)
                return;

            if (task.EndTime != null && task.EndTime < DateTime.UtcNow)
            {
                task.EndTime = DateTime.UtcNow;
                RemoveTask(task);
                return;
            }

            if (task.Period != null
                && task.LastRunTime != null
                && DateTime.UtcNow - task.LastRunTime < task.Period)
                return;

            try
            {
                task.Action();
                task.LastRunTime = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                Container.Resolve<ILogger>().LogError("An exception occured in task: " + task.Name, e);
            }

            if (task.ExecutionTarget == ExecutionTargetContext.NextFrame
                || task.ExecutionTarget == ExecutionTargetContext.NextPhysicsUpdate
                || task.ExecutionTarget == ExecutionTargetContext.Async
                || task.ExecutionTarget == ExecutionTargetContext.NextAsyncFrame
                || task.ExecutionTarget == ExecutionTargetContext.Sync)
            {
                task.EndTime = DateTime.UtcNow;
                RemoveTask(task);
            }
        }

        protected virtual void RemoveTask(IScheduledTask task)
        {
            InternalTasks.Remove((SimpleTask)task);
        }

        public void Dispose()
        {
            asyncThreadPool.Stop();
        }
    }
}