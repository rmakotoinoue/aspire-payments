import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Cenários determinísticos aceitos pela API. */
export type PaymentScenario = 'Succeed' | 'Fail' | 'FailAfterCommit' | 'FailTransient';

export interface Order {
  orderId: string;
  status: string;
  totalAmount: number;
  currency: string;
  paymentMethod: string;
  createdAt: string;
  updatedAt: string;
  traceId: string | null;
  payments: PaymentTransaction[];
  refunds: Refund[];
}

export interface PaymentTransaction {
  paymentTransactionId: string;
  orderId: string;
  idempotencyKey: string;
  amount: number;
  currency: string;
  status: string;
  paymentMethod: string;
  scenario: string;
  failureReason: string | null;
  createdAt: string;
  processedAt: string | null;
  processingRecords: ProcessingRecord[];
}

export interface ProcessingRecord {
  processingRecordId: string;
  messageId: string;
  processingStatus: string;
  attemptNumber: number;
  errorDetails: string | null;
  createdAt: string;
}

export interface Refund {
  refundId: string;
  orderId: string;
  paymentTransactionId: string | null;
  idempotencyKey: string;
  amount: number;
  status: string;
  reason: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface OrderPaymentResponse {
  orderId: string;
  paymentTransactionId: string;
  idempotencyKey: string;
  paymentStatus: string;
  orderStatus: string;
  isIdempotentReplay: boolean;
  messageId: string;
}

export interface CreateOrderRequest {
  totalAmount: number;
  currency: string;
  paymentMethod: string;
  scenario: PaymentScenario;
  idempotencyKey?: string;
}

@Injectable({ providedIn: 'root' })
export class PaymentApiService {
  private readonly http = inject(HttpClient);

  listOrders(): Observable<Order[]> {
    return this.http.get<Order[]>('/api/orders');
  }

  getOrder(orderId: string): Observable<Order> {
    return this.http.get<Order>(`/api/orders/${orderId}`);
  }

  getOrderPayments(orderId: string): Observable<PaymentTransaction[]> {
    return this.http.get<PaymentTransaction[]>(`/api/orders/${orderId}/payments`);
  }

  /**
   * Cria o pedido e solicita o pagamento. A chave de idempotência vai no
   * cabeçalho "Idempotency-Key", que é a convenção usada pela API.
   */
  createOrder(request: CreateOrderRequest, idempotencyKey?: string): Observable<OrderPaymentResponse> {
    let headers = new HttpHeaders();

    if (idempotencyKey) {
      headers = headers.set('Idempotency-Key', idempotencyKey);
    }

    return this.http.post<OrderPaymentResponse>('/api/orders', request, { headers });
  }

  requestRefund(
    orderId: string,
    amount: number,
    scenario: 'Succeed' | 'Fail',
    idempotencyKey?: string
  ): Observable<unknown> {
    let headers = new HttpHeaders();

    if (idempotencyKey) {
      headers = headers.set('Idempotency-Key', idempotencyKey);
    }

    return this.http.post(
      `/api/orders/${orderId}/refunds`,
      { amount, scenario },
      { headers }
    );
  }
}
