"""Ephemeral LAN classroom admission. IP addresses select recipients, never grant admission."""
import hashlib
import hmac
import ipaddress
import secrets
import time
from collections import OrderedDict, deque
from dataclasses import dataclass
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from .app import Account


def private_ip(value: str) -> str | None:
    try:
        ip = ipaddress.IPv4Address(value)
        if str(ip) == value and any(ip in ipaddress.IPv4Network(n) for n in (
            '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.0/8')):
            return value
    except ipaddress.AddressValueError:
        pass
    return None


def ip_user(ip: str) -> str:
    if not private_ip(ip):
        raise ValueError('Expected private IPv4')
    return 'ip-' + ip.replace('.', '-')


@dataclass
class Member:
    account: Account
    ip: str
    expires: float
    active: bool = False


class ClassroomRoom:
    MAX_MEMBERS = 100

    def __init__(self, room_id: str, join_sha256: str, host_user: str, host_ip: str, host_account: Account):
        if host_user != ip_user(host_ip) or hmac.compare_digest(join_sha256, host_account.token_sha256):
            raise ValueError('Invalid classroom host/admission policy')
        self.room_id = room_id
        self.join_sha256 = join_sha256
        self.host_user = host_user
        self.host_ip = host_ip
        self.members = {host_user: Member(host_account, host_ip, float('inf'), True)}
        self.attempts = OrderedDict()

    def prune(self):
        now = time.monotonic()
        for user, member in list(self.members.items()):
            if not member.active and member.expires <= now:
                del self.members[user]

    def account(self, user: str, source_ip: str):
        self.prune()
        member = self.members.get(user)
        if member is None or not private_ip(source_ip):
            return None
        if user != self.host_user and member.ip != source_ip:
            return None
        return member.account

    def activate(self, user: str):
        self.members[user].active = True

    def release(self, user: str):
        if user != self.host_user:
            self.members.pop(user, None)

    def authorized(self, sender: str, target: str) -> bool:
        self.prune()
        return sender != target and sender in self.members and target in self.members

    def install(self, app: FastAPI):
        @app.post('/nekochat/v1/classroom/join')
        async def join(request: Request):
            source = request.client.host if request.client else ''
            if request.query_params or request.headers.get('origin') or not private_ip(source):
                raise HTTPException(403, 'Native private LAN clients only')
            # Bound failed admission work; never trust X-Forwarded-For or a supplied IP/user.
            now = time.monotonic()
            rate = self.attempts.setdefault(source, deque())
            self.attempts.move_to_end(source)
            while len(self.attempts) > 256:
                self.attempts.popitem(last=False)
            while rate and rate[0] <= now - 10:
                rate.popleft()
            if len(rate) >= 10:
                raise HTTPException(429, 'Try again later')
            rate.append(now)
            auth = request.headers.get('authorization', '')
            if request.headers.get('x-nekochat-room') != self.room_id or not auth.startswith('Bearer ') or not hmac.compare_digest(
                hashlib.sha256(auth[7:].encode()).hexdigest(), self.join_sha256):
                raise HTTPException(401, 'Room invitation required')
            # This route has no body or client-selected identity, and is versioned independently.
            if request.headers.get('content-length', '0') != '0' or request.headers.get('transfer-encoding'):
                raise HTTPException(400, 'No request body allowed')
            self.prune()
            user = ip_user(source)
            if user in self.members:
                raise HTTPException(409, 'This IP is already in the room; leave or wait for the old connection to expire')
            if len(self.members) >= self.MAX_MEMBERS:
                raise HTTPException(503, 'Classroom is full')
            token = secrets.token_urlsafe(32)
            self.members[user] = Member(Account(token_sha256=hashlib.sha256(token.encode()).hexdigest(), friends=[]), source, now + 30)
            return JSONResponse({'protocol': 'nekochat', 'version': 1, 'room_id': self.room_id,
                'user': user, 'ip': source, 'teacher_ip': self.host_ip, 'token': token}, headers={'Cache-Control': 'no-store'})
