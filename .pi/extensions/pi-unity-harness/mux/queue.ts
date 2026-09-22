export interface MuxQueueJob<T> {
  value: T;
  dispatched: boolean;
  settled: boolean;
}

export class MuxQueue<T> {
  private readonly values: MuxQueueJob<T>[] = [];
  private active: MuxQueueJob<T> | null = null;
  push(value: T): MuxQueueJob<T> { const job = { value, dispatched: false, settled: false }; this.values.push(job); return job; }
  take(): MuxQueueJob<T> | undefined { if (this.active || this.values.length === 0) return undefined; this.active = this.values.shift()!; return this.active; }
  finish(job: MuxQueueJob<T>): void { job.settled = true; if (this.active === job) this.active = null; }
  get current(): MuxQueueJob<T> | null { return this.active; }
  get length(): number { return this.values.length; }
  drain(): MuxQueueJob<T>[] { return this.values.splice(0); }
  prepend(job: MuxQueueJob<T>): void { this.values.unshift(job); }
}
