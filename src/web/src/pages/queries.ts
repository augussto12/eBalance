import { useQuery, type QueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import type { Account, Category } from '../api/types';

export const useAccounts = (includeDeleted = false) =>
  useQuery({ queryKey: ['accounts', includeDeleted], queryFn: () => api<Account[]>('GET', `/accounts${includeDeleted ? '?includeDeleted=true' : ''}`) });

export const useCategories = (includeDeleted = false) =>
  useQuery({ queryKey: ['categories', includeDeleted], queryFn: () => api<Category[]>('GET', `/categories${includeDeleted ? '?includeDeleted=true' : ''}`) });

/** Tras cualquier cambio financiero se recalcula todo desde el servidor (autoridad de dinero). */
export const invalidateFinancial = (qc: QueryClient) => qc.invalidateQueries({ predicate: (q) => q.queryKey[0] !== 'me' });

/**
 * Cierre de sesión / 401: borra de memoria TODOS los datos financieros y marca la sesión como anónima.
 * No usa qc.clear(): eso desconectaría al observador de ['me'] y la pantalla no volvería al login.
 */
export const signOutLocally = (qc: QueryClient) => {
  qc.removeQueries({ predicate: (q) => q.queryKey[0] !== 'me' });
  qc.setQueryData(['me'], { state: 'anonymous' });
};
