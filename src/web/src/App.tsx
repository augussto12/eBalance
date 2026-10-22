import { useEffect } from 'react';
import { Navigate, Route, Routes } from 'react-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api, onUnauthorized, resetCsrf } from './api/client';
import type { Me } from './api/types';
import { LoginPage, MfaPage, EnrollPage } from './auth/AuthPages';
import { Shell } from './layout/Shell';
import { UiProvider } from './components/Ui';
import { SummaryPage } from './pages/SummaryPage';
import { MovementsPage } from './pages/MovementsPage';
import { PlansPage } from './pages/PlansPage';
import { ProjectionPage } from './pages/ProjectionPage';
import { SettingsPage } from './pages/SettingsPage';
import { signOutLocally } from './pages/queries';

export const ME_KEY = ['me'] as const;

export function App() {
  const qc = useQueryClient();
  const me = useQuery({ queryKey: ME_KEY, queryFn: () => api<Me>('GET', '/auth/me', undefined, { quiet401: true }), staleTime: 60_000 });

  useEffect(() => {
    // 401 en cualquier request privado: limpiar toda la caché (datos financieros) y volver al login.
    onUnauthorized(() => {
      resetCsrf();
      signOutLocally(qc);
    });
  }, [qc]);

  if (me.isPending) return <div className="auth"><div className="loading">Conectando con tu servidor…</div></div>;
  if (me.isError)
    return (
      <div className="auth">
        <div className="card errorbox">
          No hay conexión con el servidor.
          <br />
          <button onClick={() => me.refetch()}>Reintentar</button>
        </div>
      </div>
    );

  switch (me.data.state) {
    case 'anonymous':
      return <LoginPage />;
    case 'mfa_required':
      return <MfaPage />;
    case 'enroll_required':
      return <EnrollPage />;
    default:
      return (
        <UiProvider me={me.data}>
          <Routes>
            <Route element={<Shell me={me.data} />}>
              <Route index element={<SummaryPage />} />
              <Route path="movimientos" element={<MovementsPage />} />
              <Route path="tarjetas" element={<PlansPage kind="card" />} />
              <Route path="prestamos" element={<PlansPage kind="loan" />} />
              <Route path="proyeccion" element={<ProjectionPage />} />
              <Route path="configuracion" element={<SettingsPage me={me.data} />} />
              <Route path="*" element={<Navigate to="/" replace />} />
            </Route>
          </Routes>
        </UiProvider>
      );
  }
}
