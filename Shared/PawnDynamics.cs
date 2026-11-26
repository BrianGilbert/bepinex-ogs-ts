#nullable enable
using System;
using System.Numerics;
using Ogs.Telemetry;

namespace Ogs.BepInEx
{
    public sealed class PawnDynamics
    {
        readonly BodyDynamicsDeriver _deriver = new BodyDynamicsDeriver();
        readonly VelocityCheck _check = new VelocityCheck();
        double _clock;

        public Vector3 SpecificForce => _deriver.SpecificForce;
        public Vector3 AngularVelocity => _deriver.AngularVelocity;
        public bool UsingEngineVelocity { get; private set; }
        public VelocityCheck Check => _check;

        public Vector3 GravityVector
        {
            get => _deriver.GravityVector;
            set => _deriver.GravityVector = value;
        }

        public void Restart(float deltaTime, Vector3 position, Quaternion orientation)
        {
            _clock += deltaTime;
            _check.Restart();
            UsingEngineVelocity = false;
            _deriver.Reset(position, orientation, _clock);
        }

        public void Sample(float deltaTime, Vector3 position, Quaternion orientation, Vector3? velocity)
        {
            _clock += deltaTime;
            bool useEngine = velocity.HasValue && _check.Update(deltaTime, position, velocity.Value);
            if (useEngine != UsingEngineVelocity)
            {
                UsingEngineVelocity = useEngine;
                _deriver.Reset(position, orientation, _clock);
                return;
            }
            _deriver.Sample(_clock, position, orientation, useEngine, velocity ?? default);
        }
    }

    public sealed class VelocityCheck
    {
        public const int FailuresBeforeDistrust = 3;
        public const int MaxFrozenWindowsInARow = 6;
        const float FrozenShareOfClaimed = 0.1f;
        const double WindowSeconds = 0.5;
        const float AllowedErrorOfPath = 0.5f;
        const float AllowedErrorMetres = 0.5f;

        Vector3 _previous, _unexplained;
        float _pathMetres, _claimedMetres;
        int _frozenInARow;
        double _seconds;
        bool _hasPrevious;

        public bool Trusted { get; private set; } = true;
        public int Windows { get; private set; }
        public int FailuresInARow { get; private set; }
        public float UnexplainedMetres { get; private set; }
        public float AllowedMetres { get; private set; }
        public float PathMetres { get; private set; }
        public float ClaimedMetres { get; private set; }
        public bool SkippedAsFrozen { get; private set; }

        public void Restart()
        {
            _unexplained = default;
            _pathMetres = 0;
            _seconds = 0;
            _hasPrevious = false;
            _claimedMetres = 0;
            _frozenInARow = 0;
            SkippedAsFrozen = false;
            FailuresInARow = 0;
        }

        public bool Update(double deltaTime, Vector3 position, Vector3 velocity)
        {
            if (_hasPrevious)
            {
                var moved = position - _previous;
                _unexplained += moved - velocity * (float)deltaTime;
                _pathMetres += moved.Length();
                _claimedMetres += velocity.Length() * (float)deltaTime;
                _seconds += deltaTime;
            }
            _previous = position;
            _hasPrevious = true;
            if (_seconds >= WindowSeconds)
            {
                UnexplainedMetres = _unexplained.Length();
                AllowedMetres = AllowedErrorOfPath * _pathMetres + AllowedErrorMetres;
                PathMetres = _pathMetres;
                ClaimedMetres = _claimedMetres;
                Windows++;
                bool failed = UnexplainedMetres > AllowedMetres;
                bool frozen = failed && PathMetres < FrozenShareOfClaimed * ClaimedMetres;
                _frozenInARow = frozen ? _frozenInARow + 1 : 0;
                SkippedAsFrozen = frozen && _frozenInARow <= MaxFrozenWindowsInARow;
                if (!failed)
                {
                    FailuresInARow = 0;
                    Trusted = true;
                }
                else if (!SkippedAsFrozen && ++FailuresInARow >= FailuresBeforeDistrust)
                    Trusted = false;
                _claimedMetres = 0;
                _unexplained = default;
                _pathMetres = 0;
                _seconds = 0;
            }
            return Trusted;
        }
    }

    public static class VelocityReader
    {
        static readonly string[] PropertyNames = { "linearVelocity", "velocity" };

        public static Func<Vector3?>? For(Type bodyType, object body)
        {
            foreach (var name in PropertyNames)
            {
                var property = bodyType.GetProperty(name);
                var vector = property?.PropertyType;
                var x = vector?.GetField("x");
                var y = vector?.GetField("y");
                var z = vector?.GetField("z");
                if (property == null || x == null || y == null || z == null) continue;
                Func<Vector3?> read = () =>
                {
                    try
                    {
                        var v = property.GetValue(body);
                        return new Vector3((float)x.GetValue(v)!, (float)y.GetValue(v)!, (float)z.GetValue(v)!);
                    }
                    catch { return null; }
                };
                if (read() != null) return read;
            }
            return null;
        }
    }
}
