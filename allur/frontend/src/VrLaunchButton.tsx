import {useEffect, useRef, useState} from 'react';
import {Icon} from './Icons';
import {post, request} from './api';
import {useCanWrite} from './permissions';
import './vr-launch.css';

type Status = {available: boolean; running: boolean; starting?: boolean; reason: string; message?: string};

export function VrLaunchButton() {
  const canWrite = useCanWrite();
  const [status, setStatus] = useState<Status | null>(null);
  const [busy, setBusy] = useState(false), [message, setMessage] = useState(''), [error, setError] = useState('');
  const alive = useRef(true), launching = useRef(false);
  useEffect(() => {
    alive.current = true; let inFlight = false;
    async function refresh() {
      if (inFlight || launching.current) return;
      inFlight = true;
      try {const value = await request<Status>('/fusion/vr'); if (alive.current) {setStatus(value); setMessage(value.message || ''); if (value.running || value.starting) setError('');}}
      catch {if (alive.current) setStatus(null);}
      finally {inFlight = false;}
    }
    void refresh(); const timer = setInterval(() => {if (!document.hidden) void refresh();}, 5000);
    return () => {alive.current = false; clearInterval(timer);};
  }, []);
  async function launch() {
    if (launching.current || !canWrite) return;
    launching.current = true; setBusy(true); setError(''); setMessage('');
    try {const result = await post<Status>('/fusion/vr/launch', {}); if (alive.current) {setStatus(result); setMessage(result.message || 'VR запущен. Наденьте очки.');}}
    catch (cause) {if (alive.current) setError((cause as Error).message);}
    finally {launching.current = false; if (alive.current) setBusy(false);}
  }
  return <div className="vr-launch">
    <button type="button" className="button vr-launch-button" onClick={() => void launch()} disabled={busy || status?.starting || !canWrite}
      aria-describedby="vr-launch-status" data-testid="launch-vr" aria-busy={busy}>
      <Icon name="headset" size={21}/>{busy || status?.starting ? 'Запускаем VR…' : status?.running ? 'Открыть VR' : 'Запустить VR'}
      {!busy && <Icon name="arrowUp" size={15}/>}
    </button>
    <span id="vr-launch-status" className={`vr-launch-status ${error ? 'error' : ''}`} role="status" aria-live="polite">
      {error || message || (!canWrite ? 'Нужна роль оператора' : status?.running ? 'Приложение уже работает' : status && !status.available ? status.reason : 'Откроется цех на этом компьютере')}
    </span>
  </div>;
}
