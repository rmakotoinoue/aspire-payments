import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { Observable, EMPTY } from 'rxjs';

/**
 * Notificação de mudança de status de pagamento publicada pelo worker e
 * entregue pela API via Server-Sent Events.
 */
export interface PaymentStatusChanged {
  orderId: string;
  paymentTransactionId: string;
  idempotencyKey: string;
  orderStatus: string;
  paymentStatus: string;
  amount: number;
  currency: string;
  failureReason?: string | null;
  occurredAt: string;
  traceId: string;
}

@Injectable({ providedIn: 'root' })
export class PaymentEventsService {
  private readonly platformId = inject(PLATFORM_ID);

  /**
   * Stream de mudanças de status. O endpoint usa SSE com o tipo de evento
   * "status", então o listener precisa ser registrado pelo nome do evento.
   */
  connect(): Observable<PaymentStatusChanged> {
    // EventSource só existe no browser. Durante o SSR (Node) o stream é
    // suprimido para não quebrar a renderização.
    if (!isPlatformBrowser(this.platformId)) {
      return EMPTY;
    }

    return new Observable<PaymentStatusChanged>(observer => {
      const source = new EventSource('/api/events');

      source.onopen = () => {
        console.log('Conexão SSE de pagamentos estabelecida');
      };

      // Eventos padrão, caso a API envie "data:" sem "event: status".
      source.onmessage = event => {
        this.handleEvent(event, observer);
      };

      // Eventos nomeados "event: status".
      source.addEventListener('status', event => {
        this.handleEvent(event as MessageEvent, observer);
      });

      source.onerror = error => {
        console.error('Erro na conexão SSE de pagamentos:', error);
        // O EventSource tenta reconectar automaticamente.
      };

      return () => source.close();
    });
  }

  private handleEvent(
    event: MessageEvent,
    observer: import('rxjs').Subscriber<PaymentStatusChanged>
  ): void {
    try {
      const data = JSON.parse(event.data) as PaymentStatusChanged;
      observer.next(data);
    } catch (error) {
      console.error('Erro ao interpretar evento SSE de pagamento:', event.data, error);
    }
  }
}
