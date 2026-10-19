"""Single-use local desktop handoff. Never enabled by the network deployment."""
import hashlib
import hmac
import os
import secrets
import time

from fastapi import APIRouter, HTTPException, Request, Response
from pydantic import Field
from sqlalchemy import delete, select
from .database import User, LoginSession
from .security import StrictBody, COOKIE, public_user, audit

router = APIRouter()


def enabled():
    return os.getenv('ALLUR_DESKTOP') == '1' and len(os.getenv('ALLUR_DESKTOP_SECRET', '')) >= 40


def local(request):
    if not enabled() or not request.client or request.client.host not in ('127.0.0.1', '::1'):
        raise HTTPException(404, 'Недоступно')
    return request.app.state.security


@router.post('/api/desktop/ticket')
def ticket(request: Request):
    auth = local(request)
    auth.limited('desktop-ticket', 60)
    if not hmac.compare_digest(request.headers.get('x-desktop-secret', ''), os.environ['ALLUR_DESKTOP_SECRET']):
        raise HTTPException(403, 'Недопустимый запуск')
    raw = secrets.token_urlsafe(32)
    with auth.lock:
        pending = getattr(auth, 'desktop_tickets', {})
        pending = {k: v for k, v in pending.items() if v > time.monotonic()}
        if len(pending) >= 60:
            raise HTTPException(429, 'Подождите минуту')
        pending[hashlib.sha256(raw.encode()).hexdigest()] = time.monotonic() + 60
        auth.desktop_tickets = pending
    return {'ticket': raw}


class Handoff(StrictBody):
    ticket: str = Field(min_length=32, max_length=100)


@router.post('/api/desktop/session')
def session(body: Handoff, request: Request, response: Response):
    auth = local(request)
    auth.limited('desktop-session', 60)
    with auth.lock:
        expiry = getattr(auth, 'desktop_tickets', {}).pop(hashlib.sha256(body.ticket.encode()).hexdigest(), 0)
        if expiry <= time.monotonic():
            raise HTTPException(401, 'Ссылка истекла. Нажмите «Открыть панель» в AllurTwin.exe.')
        with auth.sessions.begin() as db:
            user = db.scalar(select(User).where(User.username == 'admin'))
            if not user or not user.active:
                raise HTTPException(403, 'Локальная учётная запись отключена')
            raw, csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
            db.execute(delete(LoginSession).where(LoginSession.expires <= time.time()))
            existing = db.scalars(select(LoginSession).where(LoginSession.user_id == user.id).order_by(LoginSession.expires.desc())).all()
            for row in existing[9:]:
                db.delete(row)
            db.add(LoginSession(id=hashlib.sha256(raw.encode()).hexdigest(), user_id=user.id, csrf=csrf, expires=time.time()+8*3600))
            audit(db, 'desktop-login', 'One-time local desktop handoff')
            result = {**public_user(user), 'csrf': csrf}
    auth.invalidate()
    response.set_cookie(COOKIE, raw, max_age=8*3600, httponly=True, secure=auth.secure, samesite='strict', path='/')
    return result
