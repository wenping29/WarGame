import { useGameStore } from '../store/gameStore'
import HexMap from '../components/HexMap'

export default function DirectorView() {
  const { turn, maxTurns, units } = useGameStore()

  // Mock data for testing (will be replaced with API data)
  const mockCells = Array.from({ length: 20 }, (_, i) => ({
    q: i % 10,
    r: Math.floor(i / 10),
    terrain: ['clear', 'woods', 'hills', 'mountain'][i % 4],
    elev: 50 + (i * 10),
    slope: 2 + (i % 3),
    road: i % 5 === 0,
    river: i > 15 && i < 18,
    bridge: false,
    city: i === 5 ? 2 : 0,
    railway: false,
    railStation: false,
    tunnel: false,
    powerline: false,
    powerPlant: false,
    substation: false,
    airport: 0,
  }))

  return (
    <div className="page director-page">
      <h2>导演部视角</h2>
      <div className="director-info">
        <span className="muted">回合: {turn}/{maxTurns}</span>
        <span className="muted">单位数: {units.length}</span>
      </div>
      <div className="map-container">
        <HexMap
          width={10}
          height={10}
          cells={mockCells}
          units={units}
          obsPoints={{}}
        />
      </div>
    </div>
  )
}
