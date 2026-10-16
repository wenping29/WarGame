import { useGameStore } from '../store/gameStore'
import HexMap from '../components/HexMap'
import UnitInteraction from '../components/UnitInteraction'
import OrderSubmission from '../components/OrderSubmission'
import { submitOrders, advanceTurn } from '../services/api'

export default function DirectorView() {
  const { 
    turn, 
    maxTurns, 
    units, 
    selectedUnit, 
    selectUnit, 
    currentSide,
    pendingOrders,
    validationErrors,
    isSubmitting,
    removeOrder,
    clearValidationErrors,
    setValidationErrors,
    setSubmitting,
    clearOrders,
    nextTurn,
  } = useGameStore()

  // Mock data for testing (will be replaced with API data)
  const mockCells = Array.from({ length: 100 }, (_, i) => ({
    q: i % 10,
    r: Math.floor(i / 10),
    terrain: ['clear', 'woods', 'hills', 'mountain', 'clear', 'clear', 'woods', 'clear'][i % 8],
    elev: 50 + (i * 5),
    slope: 2 + (i % 3),
    road: i % 7 === 0,
    river: i > 80 && i < 85,
    bridge: i === 82,
    city: i === 25 ? 2 : i === 65 ? 1 : 0,
    railway: i % 15 === 0,
    railStation: i === 30 || i === 70,
    tunnel: false,
    powerline: false,
    powerPlant: false,
    substation: false,
    airport: i === 45 ? 1 : 0,
  }))

  // Handle order submission to backend
  const handleSubmitOrders = async () => {
    if (!currentSide || pendingOrders.length === 0) return
    
    setSubmitting(true)
    try {
      const response = await submitOrders(currentSide, pendingOrders)
      
      // Set validation errors from rejected orders
      if (response.rejected && response.rejected.length > 0) {
        setValidationErrors(response.rejected)
      } else {
        clearValidationErrors()
        // Clear pending orders on success
        clearOrders()
      }
    } catch (error) {
      console.error('Failed to submit orders:', error)
      setValidationErrors([{
        unitId: 'system',
        reason: 'network_error',
        message: '提交失败，请检查网络连接',
      }])
    } finally {
      setSubmitting(false)
    }
  }

  // Handle turn advancement
  const handleAdvanceTurn = async () => {
    setSubmitting(true)
    try {
      await advanceTurn()
      nextTurn()
      clearOrders()
      clearValidationErrors()
    } catch (error) {
      console.error('Failed to advance turn:', error)
      setValidationErrors([{
        unitId: 'system',
        reason: 'network_error',
        message: '推进回合失败',
      }])
    } finally {
      setSubmitting(false)
    }
  }

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
          selectedUnit={selectedUnit || undefined}
          obsPoints={{}}
          onUnitClick={selectUnit}
        />
      </div>
      
      {/* Unit Interaction System */}
      <UnitInteraction
        units={units}
        cells={mockCells}
        selectedUnit={selectedUnit}
      />
      
      {/* Order Submission System */}
      <OrderSubmission
        pendingOrders={pendingOrders}
        currentSide={currentSide}
        onRemoveOrder={removeOrder}
        onSubmitOrders={handleSubmitOrders}
        onAdvanceTurn={handleAdvanceTurn}
        isSubmitting={isSubmitting}
        validationErrors={validationErrors}
        clearValidationErrors={clearValidationErrors}
      />
    </div>
  )
}
