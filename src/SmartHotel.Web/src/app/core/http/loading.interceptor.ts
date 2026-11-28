import { inject } from '@angular/core';
import type { HttpInterceptorFn } from '@angular/common/http';
import { defer, finalize } from 'rxjs';

import { LoadingService } from './loading.service';

/**
 * Counts every `HttpClient` request in {@link LoadingService}, which drives the
 * global loading indicator.
 *
 * The counter is raised when the request observable is subscribed and lowered
 * in `finalize`, which runs once whether the request completes, fails or is
 * cancelled by unsubscription (for example a `switchMap` dropping an older
 * search), so every increment is balanced and the indicator never sticks.
 * The downstream call runs inside `defer`, so a later interceptor or the
 * backend that throws synchronously, before returning an observable, reaches
 * the caller as an error notification and `finalize` still lowers the counter.
 *
 * Register it first in `withInterceptors([...])` in `app.config.ts`, so it is
 * the outermost interceptor and also counts requests that later interceptors
 * clone. It applies to every request without filtering and never modifies a
 * request, a response or an error: errors pass through untouched to the caller.
 */
export const loadingInterceptor: HttpInterceptorFn = (req, next) => {
  const loading = inject(LoadingService);
  return defer(() => {
    loading.begin();
    return next(req);
  }).pipe(finalize(() => loading.end()));
};
