/**
 * 房间页：/room/:code/:role。建立 /ws 连接并按服务端 view.kind 分发视图。
 * 大厅接管确认信息经 sessionStorage（hf.join.<code>）传入（角色/昵称/force）。
 */

import { useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { RoomProvider, useRoom } from '../room/RoomSocket'
import SideView from './SideView'
import DirectorView from './DirectorView'
import SpectatorView from './SpectatorView'

interface JoinInfo {
  role?: string
  name?: string
  force?: boolean
}

export function readJoinInfo(code: string): JoinInfo {
  try {
    return JSON.parse(sessionStorage.getItem('hf.join.' + code) || '{}') as JoinInfo
  } catch {
    return {}
  }
}

export default function RoomPage() {
  const { code = '', role = '' } = useParams()
  const [sp] = useSearchParams()
  const join = readJoinInfo(code)
  const effectiveRole = role || join.role || 'director'
  const name = sp.get('name') || join.name || undefined

  return (
    <RoomProvider key={`${code}:${effectiveRole}:${join.force ? 1 : 0}`} code={code} role={effectiveRole} name={name} force={join.force}>
      <RoomBody />
    </RoomProvider>
  )
}

function RoomBody() {
  const room = useRoom()
  const navigate = useNavigate()

  if (room.status === 'missing') {
    return (
      <div className="page">
        <div className="card" style={{ maxWidth: 460, margin: '60px auto', textAlign: 'center' }}>
          <h3>房间不存在</h3>
          <p className="muted">{room.error || `副本 ${room.code} 未开启`}</p>
          <button className="btn btn-primary" onClick={() => navigate('/lobby')}>返回大厅</button>
        </div>
      </div>
    )
  }

  if (room.status === 'kicked') {
    return (
      <div className="page">
        <div className="card" style={{ maxWidth: 460, margin: '60px auto', textAlign: 'center' }}>
          <h3>连接被顶替</h3>
          <p className="muted">{room.error || '本角色已在其他终端打开'}</p>
          <button className="btn btn-primary" onClick={() => room.rejoin(true)}>强行接管本角色</button>
          <div style={{ height: 8 }} />
          <button className="btn" onClick={() => navigate('/lobby')}>返回大厅</button>
        </div>
      </div>
    )
  }

  if (!room.view) {
    return (
      <div className="page" style={{ textAlign: 'center', paddingTop: 80 }}>
        <div className="muted">
          {room.status === 'connecting' ? `连接房间 ${room.code}…` : `等待服务端视图…（${room.status}）`}
        </div>
      </div>
    )
  }

  switch (room.view.kind) {
    case 'side':
      return <SideView />
    case 'director':
      return <DirectorView />
    case 'spectator':
      return <SpectatorView />
    default:
      return <div className="page">未知视图类型</div>
  }
}
