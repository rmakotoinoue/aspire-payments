
import { Service } from '@angular/core';
import { Subject } from 'rxjs';

export interface ToastMessage {
  id: number;
  message: string;
}

@Service()
export class ToastService {
  private nextId = 0;
  private readonly toastSubject = new Subject<ToastMessage>();

  readonly notifications$ = this.toastSubject.asObservable();

  show(message: string): void {
    this.toastSubject.next({
      id: ++this.nextId,
      message
    });
  }
}
