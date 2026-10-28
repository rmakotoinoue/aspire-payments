import {
  Component,
  DestroyRef,
  inject,
  OnInit,
  signal
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { ToastMessage, ToastService } from '../services/toasted.service';

@Component({
  selector: 'app-toast-container',
  template: `
    <div class="toast-container" aria-live="polite" aria-atomic="false">
      @for (toast of toasts(); track toast.id) {
        <div class="toast">
          <span class="toast-icon">✓</span>
          <span>{{ toast.message }}</span>
          <button
            type="button"
            aria-label="Fechar notificação"
            (click)="remove(toast.id)">
            ×
          </button>
        </div>
      }
    </div>
  `,
  styleUrl: './toasted-container.component.css'
})
export class ToastContainerComponent implements OnInit {
  private readonly toastService = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  /**
   * Estado em signal para que a leitura no template marque a view como suja.
   * A aplicação roda em modo zoneless: mutar uma propriedade comum dentro de
   * um `subscribe` não agenda change detection e os toasts nunca apareceriam.
   */
  readonly toasts = signal<ToastMessage[]>([]);

  private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();

  ngOnInit(): void {
    this.toastService.notifications$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(toast => {
        this.toasts.update(current => [...current, toast]);

        const timer = setTimeout(() => {
          this.remove(toast.id);
        }, 4000);

        this.timers.set(toast.id, timer);
      });

    this.destroyRef.onDestroy(() => {
      this.timers.forEach(timer => clearTimeout(timer));
      this.timers.clear();
    });
  }

  remove(id: number): void {
    const timer = this.timers.get(id);

    if (timer) {
      clearTimeout(timer);
      this.timers.delete(id);
    }

    this.toasts.update(current => current.filter(toast => toast.id !== id));
  }
}
