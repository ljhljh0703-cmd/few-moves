using System;
using Nectorial.SlideEscape;

namespace Nectorial.SlideEscape.Unity
{
    // Presentation-only contract shared by the 2D board and the 3D preview board.
    // Implementations draw state they are given; they never change rules or state.
    internal interface IBoardView : IDisposable
    {
        void Render(RoomDefinition room, GameState state, int selectedPieceIndex);
        bool BeginTransition(RoomDefinition room, GameState before, GameState after, int selectedPieceIndex);
        void AdvanceTransition(float progress);
        void CompleteTransition(RoomDefinition room, GameState state, int selectedPieceIndex);
        void CancelTransition();
    }
}
